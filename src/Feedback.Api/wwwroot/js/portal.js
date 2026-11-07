// Shared portal helpers. All user text goes into the DOM through textContent (via el), never innerHTML.

export const CATEGORIES = ["Finding spots", "Adding & editing spots", "Spot details", "Visits", "Profile & account", "Social", "Other"];

/** el("p", {class: "x"}, "text", child) — strings become text nodes. */
export function el(tag, attrs = {}, ...children) {
  const node = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs)) {
    if (v === false || v == null) continue;
    if (k.startsWith("on")) node.addEventListener(k.slice(2), v);
    else node.setAttribute(k, v === true ? "" : v);
  }
  for (const c of children.flat()) {
    if (c == null || c === false) continue;
    node.append(c instanceof Node ? c : document.createTextNode(String(c)));
  }
  return node;
}

const TOKEN_KEY = "studease.browserToken";
let memoryToken = null;

/** The random token this browser made once (kept in localStorage); it tells upvoters apart, nothing more. */
export function browserToken() {
  try {
    let t = localStorage.getItem(TOKEN_KEY);
    if (!t) { t = makeToken(); localStorage.setItem(TOKEN_KEY, t); }
    return t;
  } catch {
    return (memoryToken ??= makeToken());
  }
}

function makeToken() {
  if (crypto.randomUUID) return crypto.randomUUID();
  return Array.from(crypto.getRandomValues(new Uint8Array(16)), (b) => b.toString(16).padStart(2, "0")).join("");
}

/** fetch() that always sends this browser's token, so the API can say which requests it upvoted. */
export function apiFetch(url, options = {}) {
  return fetch(url, { ...options, headers: { ...options.headers, "X-Browser-Token": browserToken() } });
}

/** The ▲ count pill. Click toggles this browser's Upvote; the pill shows the server's answer. */
export function upvoteButton(r) {
  const n = el("span", { class: "n" }, r.upvotes);
  const button = el("button", { type: "button", class: "up", "aria-pressed": String(r.upvoted) },
    el("span", { class: "arrow", "aria-hidden": "true" }, "▲"), n);
  const label = () => button.setAttribute("aria-label",
    `${r.upvoted ? "Take back your upvote for" : "Upvote"} ${r.title} (${r.upvotes})`);
  label();
  button.addEventListener("click", async (e) => {
    e.stopPropagation();
    button.disabled = true;
    try {
      const res = await apiFetch(`/api/requests/${r.id}/upvote`, { method: r.upvoted ? "DELETE" : "POST" });
      if (res.ok) {
        const state = await res.json();
        r.upvotes = state.upvotes;
        r.upvoted = state.upvoted;
        n.textContent = r.upvotes;
        button.setAttribute("aria-pressed", String(r.upvoted));
        label();
        toast(r.upvoted ? "Upvoted, thanks!" : "Upvote taken back");
      } else if (res.status === 429) toast("That's a lot of upvotes just now. Please try again in a minute.");
      else toast("We couldn't save your upvote. Please try again.");
    } catch {
      toast("We couldn't reach Studease. Check your connection and try again.");
    }
    button.disabled = false;
  });
  return button;
}

let toastNode = null;
export function toast(message) {
  toastNode ??= document.body.appendChild(el("div", { class: "toast", role: "status" }));
  toastNode.textContent = message;
  toastNode.classList.add("show");
  clearTimeout(toastNode.hideTimer);
  toastNode.hideTimer = setTimeout(() => toastNode.classList.remove("show"), 2200);
}

export function formatDate(iso) {
  return new Date(iso).toLocaleDateString("en-GB", { day: "numeric", month: "short" });
}

/**
 * Wires the header's "+ Request a feature" button to the pop-up form.
 * onChanged runs after a successful submit (with the new request) or after upvoting a similar request instead.
 */
export function setUpRequestForm(onChanged) {
  const open = document.querySelector("[data-open-form]");
  open.addEventListener("click", () => showForm(open, onChanged));
}

function showForm(opener, onSubmitted) {
  const close = () => {
    similar.stop();
    scrim.remove(); modal.remove(); document.removeEventListener("keydown", onKey); opener.focus();
  };
  const onKey = (e) => { if (e.key === "Escape") close(); };

  const field = (name, label, control) => el("label", { class: "field", "data-field": name },
    el("span", { class: "label" }, label), control, el("span", { class: "err", hidden: true }));

  const title = el("input", { name: "title", required: true, maxlength: 120, placeholder: "e.g. Filter spots by opening hours" });
  const description = el("textarea", { name: "description", required: true, maxlength: 2000 });
  const category = el("select", { name: "category", required: true },
    el("option", { value: "", selected: true, disabled: true }, "Choose the part of the app…"),
    CATEGORIES.map((c) => el("option", { value: c }, c)));
  const email = el("input", { name: "email", type: "email", required: true, maxlength: 254, placeholder: "So we can update you" });
  const send = el("button", { class: "btn" }, "Send request");
  const formError = el("p", { class: "form-error", role: "alert", hidden: true });
  const slot = el("div", { class: "slot", "aria-live": "polite" });
  const similar = similarAsYouType({
    title, description, slot, send,
    onUpvoted: (r) => {
      close();
      toast(`Upvoted “${r.title}”. Thanks, no need to send a new one.`);
      onSubmitted?.();
    },
  });

  const scrim = el("div", { class: "scrim", onclick: close });
  const modal = el("form", { class: "modal", role: "dialog", "aria-modal": "true", "aria-label": "Request a feature", novalidate: true },
    el("div", { class: "top" },
      el("h2", {}, "Request a feature"),
      el("button", { type: "button", class: "x", "aria-label": "Close", onclick: close }, "×")),
    field("title", "Title", title),
    field("description", "What do you need, and why?", description),
    slot,
    el("div", { class: "row" },
      field("category", "Part of the app", category),
      field("email", "Your email", email)),
    formError,
    el("div", {}, send));

  modal.addEventListener("submit", async (e) => {
    e.preventDefault();
    clearErrors(modal);
    formError.hidden = true;
    send.disabled = true;
    try {
      const res = await apiFetch("/api/requests", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ title: title.value, description: description.value, category: category.value, email: email.value }),
      });
      if (res.status === 201) {
        const created = await res.json();
        similar.stop();
        showThanks(modal, close);
        onSubmitted?.(created);
        return;
      }
      if (res.status === 400) showErrors(modal, (await res.json()).errors ?? {});
      else if (res.status === 429) showFormError(formError, "You've sent a lot of requests just now. Please try again in a minute.");
      else showFormError(formError, "Something went wrong. Please try again.");
    } catch {
      showFormError(formError, "We couldn't reach Studease. Check your connection and try again.");
    }
    send.disabled = false;
  });

  document.addEventListener("keydown", onKey);
  document.body.append(scrim, modal);
  title.focus();
}

// Similar requests as you type (prototype variant B): once title + description reach MIN_CHARS, search
// DEBOUNCE_MS after typing stops. Ordinary matches are a quiet list; a strong top match becomes a
// dark banner whose "Upvote this instead" takes over from Send. Any failure just shows nothing.
const SUGGEST_MIN_CHARS = 4;
const SUGGEST_DEBOUNCE_MS = 500;

function similarAsYouType({ title, description, slot, send, onUpvoted }) {
  let timer = null;
  let seq = 0; // bumps on every keystroke, so a late answer for older text is dropped
  let pending = false;
  let matches = [];
  let dismissed = false; // "Mine's different": hide the banner until they type again
  let stopped = false;

  const typedLength = () => (title.value + description.value).replace(/\s/g, "").length;

  function onInput() {
    if (stopped) return;
    dismissed = false;
    clearTimeout(timer);
    const mine = ++seq;
    if (typedLength() < SUGGEST_MIN_CHARS) { pending = false; matches = []; render(); return; }
    pending = true;
    render();
    timer = setTimeout(() => search(mine), SUGGEST_DEBOUNCE_MS);
  }

  async function search(mine) {
    let found = [];
    try {
      const res = await apiFetch("/api/suggest", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ title: title.value, description: description.value }),
      });
      if (res.ok) found = await res.json();
    } catch { /* no similar requests; the form still works */ }
    if (mine !== seq || stopped) return;
    matches = found;
    pending = false;
    render();
  }

  async function upvote(r, button) {
    button.disabled = true;
    try {
      const res = await apiFetch(`/api/requests/${r.id}/upvote`, { method: "POST" });
      if (res.ok) { onUpvoted(r); return; }
      toast(res.status === 429 ? "That's a lot of upvotes just now. Please try again in a minute." : "We couldn't save your upvote. Please try again.");
    } catch {
      toast("We couldn't reach Studease. Check your connection and try again.");
    }
    button.disabled = false;
  }

  const pill = (r) => {
    const b = el("button", { type: "button", class: "up", "aria-pressed": String(r.upvoted), "aria-label": `Upvote ${r.title} instead (${r.upvotes})` },
      el("span", { class: "arrow", "aria-hidden": "true" }, "▲"), el("span", { class: "n" }, r.upvotes));
    b.addEventListener("click", () => upvote(r, b));
    return b;
  };
  const row = (r, withDescription) => el("div", { class: "match" }, pill(r),
    el("div", {}, el("h4", {}, r.title), withDescription && el("p", {}, r.description)));

  function render() {
    const top = matches[0];
    const strong = !!top?.strong && !dismissed;
    send.textContent = strong ? "Send mine anyway" : "Send request";
    send.classList.toggle("ghost", strong);

    if (pending && !matches.length) { slot.replaceChildren(el("div", { class: "searching" }, "Looking for similar requests…")); return; }
    if (!matches.length) { slot.replaceChildren(); return; }
    if (!strong) {
      slot.replaceChildren(el("div", { class: "similar fadein" },
        el("span", { class: "caption" }, "Similar requests"), matches.map((r) => row(r, true))));
      return;
    }
    const instead = el("button", { type: "button", class: "btn" }, "▲ Upvote this instead");
    instead.addEventListener("click", () => upvote(top, instead));
    const different = el("button", { type: "button", class: "btn ghost sm" }, "Mine's different");
    different.addEventListener("click", () => { dismissed = true; render(); });
    const rest = matches.slice(1);
    slot.replaceChildren(
      el("div", { class: "strong-match fadein" },
        el("span", { class: "label" }, "Someone already asked for this"),
        el("div", {}, el("h4", {}, top.title), el("p", {}, `${top.description} · ${top.upvotes} upvote${top.upvotes === 1 ? "" : "s"}`)),
        el("div", { class: "actions" }, instead, different)),
      rest.length > 0 && el("div", { class: "similar" },
        el("span", { class: "caption" }, "Also similar"), rest.map((r) => row(r, false))));
  }

  title.addEventListener("input", onInput);
  description.addEventListener("input", onInput);
  return { stop() { stopped = true; clearTimeout(timer); } };
}

function clearErrors(form) {
  for (const f of form.querySelectorAll("[data-field]")) {
    f.classList.remove("invalid");
    f.querySelector(".err").hidden = true;
  }
}

function showErrors(form, errors) {
  let first = null;
  for (const [name, messages] of Object.entries(errors)) {
    const f = form.querySelector(`[data-field="${CSS.escape(name)}"]`);
    if (!f) continue;
    f.classList.add("invalid");
    const err = f.querySelector(".err");
    err.textContent = messages[0];
    err.hidden = false;
    first ??= f.querySelector("input,select,textarea");
  }
  first?.focus();
}

function showFormError(node, message) {
  node.textContent = message;
  node.hidden = false;
}

function showThanks(modal, close) {
  const done = el("button", { type: "button", class: "btn", onclick: close }, "Done");
  modal.replaceChildren(el("div", { class: "thanks" },
    el("span", { class: "sketch sk-cup", "aria-hidden": "true" }),
    el("h2", {}, "Thanks, we've got it"),
    el("p", { class: "muted" }, "Your request is on the board. We'll email you when it ships."),
    done));
  done.focus();
}
