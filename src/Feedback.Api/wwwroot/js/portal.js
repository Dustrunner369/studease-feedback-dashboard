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

export function formatDate(iso) {
  return new Date(iso).toLocaleDateString("en-GB", { day: "numeric", month: "short" });
}

/** Wires the header's "+ Request a feature" button to the pop-up form. onSubmitted runs after a successful submit. */
export function setUpRequestForm(onSubmitted) {
  const open = document.querySelector("[data-open-form]");
  open.addEventListener("click", () => showForm(open, onSubmitted));
}

function showForm(opener, onSubmitted) {
  const close = () => { scrim.remove(); modal.remove(); document.removeEventListener("keydown", onKey); opener.focus(); };
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

  const scrim = el("div", { class: "scrim", onclick: close });
  const modal = el("form", { class: "modal", role: "dialog", "aria-modal": "true", "aria-label": "Request a feature", novalidate: true },
    el("div", { class: "top" },
      el("h2", {}, "Request a feature"),
      el("button", { type: "button", class: "x", "aria-label": "Close", onclick: close }, "×")),
    field("title", "Title", title),
    field("description", "What do you need, and why?", description),
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
      const res = await fetch("/api/requests", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ title: title.value, description: description.value, category: category.value, email: email.value }),
      });
      if (res.status === 201) {
        const created = await res.json();
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
