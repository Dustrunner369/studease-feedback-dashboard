// Studease /admin. Hash routes: #problems[/<id>], #outbox[/sent], #metrics, #settings/ai-calls[?status=Failed&problem=<id>], #settings/criteria, #settings/archive, #settings/github.
// All user and AI text goes into the DOM through textContent (via el), never innerHTML.
import { el, CATEGORIES, toast, formatDate } from "/js/portal.js";

const STATUSES = ["New", "Under review", "Planned", "In progress", "Shipped", "Won't do"];
const PRIORITY_RANK = { High: 0, Medium: 1, Low: 2 };
const CLOSED = ["Shipped", "Won't do"];
const app = document.getElementById("app");
const top = document.getElementById("top");

// sel: ticked Problems (merge); frSel: ticked Feature Requests in the open drawer (move).
const ui = { tab: "All", sort: null, dir: 1, lastView: null, sel: new Set(), frSel: new Set(), frSelFor: null };

class SignedOut extends Error {}

/** fetch for /api/admin: JSON in and out; a 401 means the cookie is missing or stale, so show sign-in. */
async function api(path, options = {}) {
  const res = await fetch(`/api/admin${path}`, {
    ...options,
    headers: { "Content-Type": "application/json", ...options.headers },
    credentials: "same-origin",
  });
  if (res.status === 401) throw new SignedOut();
  if (!res.ok) throw new Error(`HTTP ${res.status}`);
  return res.status === 204 ? null : res.json();
}

/** Any other non-OK answer from /api/admin; detail is the server's problem detail, when it sent one. */
class RequestFailed extends Error {
  constructor(status, detail) { super(detail ?? `HTTP ${status}`); this.status = status; this.detail = detail; }
}

/**
 * A change to /api/admin (POST, PATCH, ...): { data } on success, { errors } (keyed by field) on a 400.
 * A 401 throws SignedOut; anything else not OK throws RequestFailed (with the server's detail).
 */
async function request(method, path, body) {
  const res = await fetch(`/api/admin${path}`, {
    method, headers: { "Content-Type": "application/json" }, credentials: "same-origin",
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (res.status === 401) throw new SignedOut();
  if (res.status === 400) return { errors: (await res.json()).errors ?? {} };
  if (!res.ok) throw new RequestFailed(res.status, (await res.json().catch(() => ({}))).detail);
  return { data: res.status === 204 ? null : await res.json() };
}

const post = (path, body) => request("POST", path, body);

const firstError = (errors) => Object.values(errors).flat()[0] ?? "That change wasn't saved.";

function route() {
  const [path, query = ""] = location.hash.replace(/^#/, "").split("?");
  const parts = path.split("/").filter(Boolean);
  return { view: parts[0] || "problems", sub: parts[1], params: new URLSearchParams(query) };
}

async function render() {
  const r = route();
  for (const a of document.querySelectorAll("#nav a")) {
    if (a.dataset.view === r.view) a.setAttribute("aria-current", "page");
    else a.removeAttribute("aria-current");
  }
  if (r.view !== "problems") { document.querySelector(".drawer")?.remove(); ui.sel.clear(); }
  if (r.view !== "problems") ui.lastView = r.view;
  closeModal();
  try {
    if (r.view === "problems") await problemsView(r.sub ? Number(r.sub) : null);
    else if (r.view === "outbox") await outboxView(r.sub === "sent" ? "sent" : "drafts");
    else if (r.view === "metrics") await metricsView();
    else if (r.view === "settings") await (r.sub === "criteria" ? criteriaView() : r.sub === "archive" ? archiveView()
      : r.sub === "github" ? githubView() : settingsView(r.params));
    else location.hash = "#problems";
    top.hidden = false;
  } catch (e) {
    if (e instanceof SignedOut) return signIn();
    app.replaceChildren(el("div", { class: "empty" },
      el("span", { class: "sketch sk-cat", "aria-hidden": "true" }),
      el("p", {}, "Something went wrong loading this page."),
      el("button", { class: "btn sm", type: "button", onclick: render }, "Try again")));
  }
}

// ---------- sign-in ----------
function signIn() {
  top.hidden = true;
  document.querySelector(".drawer")?.remove();
  const key = el("input", { type: "password", name: "key", autocomplete: "current-password", required: true, "aria-label": "Admin key", placeholder: "Admin key" });
  const error = el("p", { class: "notice err", role: "alert", hidden: true });
  const button = el("button", { class: "btn" }, "Sign in");
  const form = el("form", {}, key, error, button);
  form.addEventListener("submit", async (e) => {
    e.preventDefault();
    error.hidden = true;
    button.disabled = true;
    try {
      const res = await fetch("/api/admin/login", {
        method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ key: key.value }),
      });
      if (res.ok) { key.value = ""; return render(); }
      error.textContent = res.status === 429 ? "Too many tries. Wait a minute and try again." : "That key isn't right.";
    } catch {
      error.textContent = "Couldn't reach the server. Try again.";
    }
    error.hidden = false;
    button.disabled = false;
    key.select();
  });
  app.replaceChildren(el("div", { class: "signin reveal" },
    el("span", { class: "sketch sk-books", "aria-hidden": "true" }),
    el("span", { class: "wordmark" }, "Studease"),
    el("h1", { style: "font-size:22px" }, "Feature Intelligence admin"),
    el("p", { class: "muted" }, "Enter the admin key to continue."),
    form));
  key.focus();
}

document.getElementById("logout").addEventListener("click", async () => {
  await fetch("/api/admin/logout", { method: "POST" }).catch(() => {});
  signIn();
});

// ---------- Problems ----------
async function problemsView(openId) {
  // Opening the Problems page re-reads linked GitHub issues first, so the drawer can say "Issue closed. Mark Shipped?".
  // Redraws within the page (after a save, opening a drawer) don't refresh again.
  if (ui.lastView !== "problems") {
    await api("/github/refresh", { method: "POST" }).catch((e) => { if (e instanceof SignedOut) throw e; });
    ui.lastView = "problems";
  }
  const query = ui.tab === "All" ? "" : `?category=${encodeURIComponent(ui.tab)}`;
  const page = await api(`/problems${query}`);
  const rows = sorted(page.problems);

  const tabs = el("div", { class: "tabs", role: "tablist", "aria-label": "Category" },
    ["All", ...CATEGORIES].map((c) => el("button", {
      class: "tab", role: "tab", type: "button", "aria-selected": String(ui.tab === c),
      onclick: () => { ui.tab = c; render(); },
    }, c)));

  const th = (key, label, cls) => el("th", { class: cls, "aria-sort": ui.sort === key ? (ui.dir > 0 ? "ascending" : "descending") : null },
    el("button", { type: "button", onclick: () => sortBy(key) }, label, ui.sort === key ? (ui.dir > 0 ? " ↑" : " ↓") : ""));

  // Ticks survive re-renders, but only for Problems still in the table.
  for (const id of ui.sel) if (!page.problems.some((p) => p.id === id)) ui.sel.delete(id);
  let mergeBar = mergeBarFor(page.problems);
  const tick = (p) => el("td", { class: "tick" }, el("input", {
    type: "checkbox", checked: ui.sel.has(p.id), "aria-label": `Select ${p.title}`,
    onchange: (e) => {
      if (e.target.checked) ui.sel.add(p.id); else ui.sel.delete(p.id);
      e.target.closest("tr").classList.toggle("sel", e.target.checked);
      const next = mergeBarFor(page.problems);
      mergeBar.replaceWith(next);
      mergeBar = next;
    },
  }));

  const table = rows.length
    ? el("div", { class: "table-wrap" }, el("table", { class: "pt" },
        el("thead", {}, el("tr", {}, el("th", { class: "tick", "aria-label": "Select" }),
          th("title", "Problem"), th("status", "Status"), th("requests", "Requests", "num"), th("priority", "Priority"))),
        el("tbody", {}, rows.map((p) => el("tr", { class: [p.id === openId ? "open" : "", ui.sel.has(p.id) ? "sel" : ""].join(" ").trim() || null },
          tick(p),
          el("td", { class: "t" },
            el("button", { type: "button", onclick: () => { location.hash = `#problems/${p.id}`; } }, p.title),
            el("span", { class: "cat" }, p.category)),
          el("td", {}, p.status),
          el("td", { class: "num" }, `${p.featureRequests} · ${p.upvotes} ▲`),
          el("td", {}, priorityPill(p.priority)))))))
    : el("div", { class: "empty" }, el("span", { class: "sketch sk-cup", "aria-hidden": "true" }),
        ui.tab === "All" ? "No Problems yet. They appear as Feature Requests arrive." : `No open Problems in ${ui.tab}.`);

  const review = page.needsReview
    ? el("p", { class: "notice review-line" },
        el("button", { class: "link", type: "button", onclick: needsReview },
          `${page.needsReview} Feature Request${page.needsReview === 1 ? "" : "s"} need${page.needsReview === 1 ? "s" : ""} review`),
        `: the AI couldn't match ${page.needsReview === 1 ? "it" : "them"} to a Problem yet.`)
    : null;

  app.replaceChildren(el("div", { class: "reveal" }, review, el("div", { class: "toolbar" }, tabs), table, mergeBar));
  document.querySelector(".drawer")?.remove();
  if (openId) await drawer(openId);
}

function sortBy(key) {
  if (ui.sort === key) ui.dir = -ui.dir;
  else { ui.sort = key; ui.dir = key === "requests" ? -1 : 1; }
  render();
}

// Two or more ticked rows → Merge…; one → a hint.
function mergeBarFor(problems) {
  const picked = problems.filter((p) => ui.sel.has(p.id));
  if (picked.length === 0) return el("span", { hidden: true });
  if (picked.length === 1) return el("p", { class: "caption merge-hint" }, "Select one more to merge.");
  return el("div", { class: "mergebar", role: "region", "aria-label": "Selected Problems" },
    `${picked.length} Problems selected`,
    el("button", { class: "btn sm", type: "button", onclick: () => mergeModal(picked.map((p) => p.id)) }, "Merge…"),
    el("button", { class: "btn sm quiet", type: "button", onclick: () => { ui.sel.clear(); render(); } }, "Clear"));
}

// No sort chosen keeps the server's order (Recommended score).
function sorted(list) {
  if (!ui.sort) return list;
  const keyOf = {
    title: (p) => p.title.toLocaleLowerCase(),
    status: (p) => STATUSES.indexOf(p.status),
    requests: (p) => p.featureRequests * 3 + p.upvotes,
    priority: (p) => PRIORITY_RANK[p.priority] ?? 3,
  }[ui.sort];
  return [...list].sort((a, b) => {
    const x = keyOf(a), y = keyOf(b);
    return (x < y ? -1 : x > y ? 1 : 0) * ui.dir;
  });
}

const priorityPill = (priority) => priority
  ? el("span", { class: `pill ${priority}` }, priority)
  : el("span", { class: "pill" }, "Not set");

// ---------- Problem drawer ----------
async function drawer(id) {
  let p;
  try {
    p = await api(`/problems/${id}`);
  } catch (e) {
    if (e instanceof SignedOut) throw e;
    toast("That Problem no longer exists.");
    location.hash = "#problems";
    return;
  }
  const close = () => { location.hash = "#problems"; };
  if (ui.frSelFor !== p.id) { ui.frSel.clear(); ui.frSelFor = p.id; }
  for (const fid of ui.frSel) if (!p.featureRequests.some((r) => r.id === fid)) ui.frSel.delete(fid);
  const open = !CLOSED.includes(p.status);

  const calls = el("div", {}, el("p", { class: "caption" }, "Loading…"));
  let moveBar = open ? moveBarFor(p) : el("span", { hidden: true });
  const onTick = open ? () => { const next = moveBarFor(p); moveBar.replaceWith(next); moveBar = next; } : null;
  const aside = el("aside", { class: "drawer", "aria-label": `Problem: ${p.title}` },
    el("div", { class: "spread" }, el("span", { class: "label" }, "Problem"),
      el("button", { class: "btn sm quiet", type: "button", onclick: close }, "Close ✕")),
    titleBlock(p),
    headerControls(p),
    priorityBlock(p),
    solutionsBlock(p),
    githubBlock(p),
    el("details", { open: !!p.brief },
      el("summary", {}, p.brief?.outOfDate ? "Decision Brief · out of date" : "Decision Brief"),
      briefBlock(p)),
    el("details", { open: true },
      el("summary", {}, `Feature Requests (${p.featureRequests.length})`),
      el("div", { class: "frs" }, p.featureRequests.map((r) => requestRow(r, onTick))),
      moveBar),
    el("details", {},
      el("summary", {}, `Decision log (${p.decisions.length})`),
      p.decisions.length ? el("ul", { class: "decisions" }, p.decisions.map(decisionRow))
        : el("p", { class: "caption" }, "No decisions yet.")),
    el("details", { ontoggle: (e) => { if (e.target.open && !calls.dataset.loaded) loadCalls(calls, p.id); } },
      el("summary", {}, "AI calls"), calls));
  document.querySelector(".drawer")?.remove();
  document.body.append(aside);
  aside.querySelector("button").focus();
}

/** Saves a drawer change, then redraws the page. Closing a Problem sends it to the Archive and shuts the drawer. */
async function save(p, body, { onErrors, done } = {}) {
  try {
    const result = await request("PATCH", `/problems/${p.id}`, body);
    if (result.errors) {
      const message = firstError(result.errors);
      if (onErrors) onErrors(message); else toast(message);
      return false;
    }
    const fresh = result.data;
    if (CLOSED.includes(fresh.status) && !CLOSED.includes(p.status)) {
      toast(fresh.status === "Shipped"
        ? "Shipped: moved to the Archive. ✦ Requester updates are drafted in the Outbox."
        : `Closed as ${fresh.status}: moved to the Archive.`);
      location.hash = "#problems";
      return true;
    }
    if (done) toast(done);
    await render();
    return true;
  } catch (e) {
    if (e instanceof SignedOut) { signIn(); return false; }
    toast("Couldn't save. Try again.");
    return false;
  }
}

function titleBlock(p) {
  const view = el("div", { class: "stack", style: "gap:4px" },
    el("h2", {}, p.title),
    el("p", { class: "muted" }, p.description));
  const title = el("input", { type: "text", value: p.title, maxlength: "120", "aria-label": "Title" });
  const description = el("textarea", { rows: "3", maxlength: "2000", "aria-label": "Description" }, p.description);
  const error = el("p", { class: "notice err", role: "alert", hidden: true });
  const cancel = el("button", { class: "btn sm quiet", type: "button" }, "Cancel");
  const form = el("form", { class: "stack", style: "gap:6px", hidden: true }, title, description, error,
    el("div", { class: "row" }, el("button", { class: "btn sm" }, "Save"), cancel));
  const rename = el("button", { class: "btn sm quiet", type: "button" }, "Rename");
  form.addEventListener("submit", async (e) => {
    e.preventDefault();
    error.hidden = true;
    await save(p, { title: title.value, description: description.value }, {
      done: "Saved.", onErrors: (message) => { error.textContent = message; error.hidden = false; },
    });
  });
  cancel.addEventListener("click", () => { form.hidden = true; view.hidden = false; rename.hidden = false; });
  rename.addEventListener("click", () => { view.hidden = true; rename.hidden = true; form.hidden = false; title.focus(); });
  const mergeWith = CLOSED.includes(p.status) ? null
    : el("button", { class: "btn sm quiet", type: "button", onclick: () => mergeWithModal(p) }, "Merge with…");
  return el("div", { class: "stack", style: "gap:6px" }, view, form, el("div", { class: "row", style: "gap:6px" }, rename, mergeWith));
}

// Status and Category. Won't do asks for a one-line reason before it is saved.
function headerControls(p) {
  const status = el("select", { "aria-label": "Status" },
    STATUSES.map((s) => el("option", { value: s, selected: s === p.status }, s)));
  const category = el("select", { "aria-label": "Category" },
    CATEGORIES.map((c) => el("option", { value: c, selected: c === p.category }, c)));
  const reason = el("input", { type: "text", maxlength: "300", "aria-label": "Reason for Won't do",
    placeholder: "Why won't we do it? One line, goes in the decision log." });
  const reasonError = el("span", { class: "caption", style: "color:var(--error)", hidden: true });
  const back = el("button", { class: "btn sm quiet", type: "button" }, "Cancel");
  const wontDo = el("form", { class: "stack", style: "gap:6px", hidden: true },
    reason, reasonError, el("div", { class: "row" }, el("button", { class: "btn sm" }, "Close as Won't do"), back));

  status.addEventListener("change", async () => {
    if (status.value === "Won't do") { wontDo.hidden = false; reason.focus(); return; }
    wontDo.hidden = true;
    status.disabled = true;
    if (!await save(p, { status: status.value }, { done: `Status: ${status.value}.` })) { status.value = p.status; status.disabled = false; }
  });
  wontDo.addEventListener("submit", async (e) => {
    e.preventDefault();
    reasonError.hidden = true;
    await save(p, { status: "Won't do", reason: reason.value }, {
      onErrors: (message) => { reasonError.textContent = message; reasonError.hidden = false; },
    });
  });
  back.addEventListener("click", () => { wontDo.hidden = true; status.value = p.status; });
  category.addEventListener("change", async () => {
    category.disabled = true;
    if (!await save(p, { category: category.value }, { done: `Category: ${category.value}.` })) { category.value = p.category; category.disabled = false; }
  });

  const closed = CLOSED.includes(p.status)
    ? el("p", { class: "notice" }, `Closed as ${p.status}${p.closedAt ? ` on ${formatDate(p.closedAt)}` : ""}: it's in the Archive and its Recommended Priority is frozen. Pick another status to reopen it.`)
    : null;
  return el("div", { class: "stack", style: "gap:8px" },
    el("div", { class: "row" },
      el("label", { class: "row", style: "gap:6px" }, el("span", { class: "label" }, "Status"), status),
      el("label", { class: "row", style: "gap:6px" }, el("span", { class: "label" }, "Category"), category)),
    wontDo, closed);
}

// ✦ Suggest Solutions: reads the connected repo and writes options. The latest result is kept; a failure shows Try again.
function solutionsBlock(p) {
  const s = p.solutions;
  const error = el("p", { class: "notice err", role: "alert", hidden: true });
  const status = el("p", { class: "caption", hidden: true }, "Reading the repo and writing options… this can take a minute.");
  const run = el("button", { class: "btn sm", type: "button" }, s ? "Run again" : "✦ Suggest Solutions");
  run.addEventListener("click", async () => {
    run.disabled = true;
    error.hidden = true;
    status.hidden = false;
    const fail = (message) => {
      status.hidden = true;
      error.replaceChildren(message, " ",
        el("button", { class: "btn sm", type: "button", onclick: () => run.click() }, "Try again"));
      error.hidden = false;
      run.disabled = false;
    };
    try {
      const result = await post(`/problems/${p.id}/solutions`);
      if (result.errors) return fail(firstError(result.errors));
      ui.solutionsOpen = p.id;
      toast("✦ Solutions written.");
      await render();
    } catch (e) {
      if (e instanceof SignedOut) return signIn();
      fail(e instanceof RequestFailed ? e.detail ?? `Suggest Solutions failed (HTTP ${e.status}).` : "Couldn't reach the server.");
    }
  });
  const result = s
    ? [
        el("p", { class: "caption" }, `Generated ${formatDate(s.generatedAt)} · read ${s.files.length} file${s.files.length === 1 ? "" : "s"}`),
        s.files.length ? el("ul", { class: "sol-files" }, s.files.map((f) => el("li", {}, el("code", {}, f)))) : null,
        el("div", { class: "sol-body" }, s.body),
      ]
    : [el("p", { class: "caption" }, "Reads the connected GitHub repo (file tree, about 8 chosen files, open issues and PRs) and writes options.")];
  return el("details", { class: "solutions", open: ui.solutionsOpen === p.id || !s },
    el("summary", {}, s ? "✦ Suggested solutions" : "✦ Suggest Solutions"),
    el("div", { class: "stack", style: "gap:6px" }, ...result, el("div", { class: "row" }, run), status, error));
}

// GitHub: Create GitHub issue (by hand), then the linked issue's state as of the last refresh.
function githubBlock(p) {
  const issue = p.gitHubIssue;
  const error = el("p", { class: "notice err", role: "alert", hidden: true });
  if (!issue) {
    const create = el("button", { class: "btn sm", type: "button" }, "Create GitHub issue");
    create.addEventListener("click", async () => {
      create.disabled = true;
      error.hidden = true;
      const failed = (message) => { error.textContent = message; error.hidden = false; create.disabled = false; };
      try {
        const result = await post(`/problems/${p.id}/github-issue`);
        if (result.errors) return failed(firstError(result.errors));
        toast(`Created GitHub issue #${result.data.gitHubIssue.number}.`);
        await render();
      } catch (e) {
        if (e instanceof SignedOut) return signIn();
        failed(e instanceof RequestFailed ? e.detail ?? `Couldn't create the issue (HTTP ${e.status}).` : "Couldn't reach the server. Try again.");
      }
    });
    return el("div", { class: "stack", style: "gap:6px" }, el("span", { class: "label" }, "GitHub"),
      el("div", { class: "row" }, create, el("span", { class: "caption" }, "Sends this Problem to engineering as an issue.")), error);
  }
  const prompt = issue.markShipped
    ? el("p", { class: "notice" }, "Issue closed. Mark Shipped? ",
        el("button", { class: "btn sm", type: "button", onclick: (e) => {
          e.target.disabled = true;
          save(p, { status: "Shipped", reason: `GitHub issue #${issue.number} closed.` });
        } }, "Mark Shipped"))
    : null;
  return el("div", { class: "stack", style: "gap:6px" }, el("span", { class: "label" }, "GitHub"),
    el("div", { class: "row" },
      el("a", { class: "link", href: issue.url, target: "_blank", rel: "noopener" }, `Issue #${issue.number}`),
      el("span", { class: `pill ${issue.state === "closed" ? "Low" : ""}` }, issue.state),
      issue.checkedAt ? el("span", { class: "caption" }, `checked ${formatDate(issue.checkedAt)}`) : null),
    prompt);
}

// ---------- Decision Brief ----------
// Generate / Regenerate / Try again; claims cite Feature Requests as chips that open the original text.
function briefBlock(p) {
  const b = p.brief;
  const n = p.featureRequests.length;
  const status = el("div", { role: "status" });
  const generate = (label, cls = "btn sm") => {
    const button = el("button", { class: cls, type: "button" }, label);
    button.addEventListener("click", async () => {
      body.querySelectorAll("button[data-gen]").forEach((x) => { x.disabled = true; });
      status.replaceChildren(el("p", { class: "caption" }, `✦ Writing the brief from ${n} Feature Request${n === 1 ? "" : "s"}…`));
      try {
        const result = await post(`/problems/${p.id}/brief`);
        if (result.errors) throw new RequestFailed(400, firstError(result.errors));
        toast("Decision Brief written.");
        await render();
      } catch (e) {
        if (e instanceof SignedOut) return signIn();
        if (!(e instanceof RequestFailed)) {
          status.replaceChildren(el("p", { class: "notice err", role: "alert" }, "Couldn't reach the server. ", generate("Try again")));
          return;
        }
        status.replaceChildren(el("p", { class: "notice err", role: "alert" },
          b ? "The brief couldn't be written. The previous brief is kept. " : "The brief couldn't be written. ",
          generate("Try again", "btn sm")));
        body.querySelectorAll("button[data-gen]").forEach((x) => { x.disabled = false; });
      }
    });
    button.dataset.gen = "1";
    return button;
  };

  const body = el("div", { class: "brief stack", style: "gap:6px" });
  if (!b) {
    body.append(el("div", { class: "row" }, generate("✦ Generate brief", "btn"),
      el("span", { class: "caption" }, `Reads all ${n} Feature Request${n === 1 ? "" : "s"} · recommends no decision`)), status);
    return body;
  }
  if (b.outOfDate) {
    body.append(el("p", { class: "notice" }, "Out of date: this Problem's Feature Requests changed since the brief was written. ",
      generate("Regenerate")));
  }
  const unverified = b.sections.flatMap((s) => s.claims).filter((c) => !c.verified).length;
  body.append(
    el("div", { class: "spread" },
      el("span", { class: "caption" }, `Written ${formatDate(b.writtenAt)} · ${b.whoIsAsking.featureRequests} Feature Requests · recommends no decision`),
      b.outOfDate ? null : generate("Regenerate", "btn sm quiet")),
    status,
    ...b.sections.map((s) => el("section", {},
      el("h4", {}, s.title),
      s.key === "whoIsAsking" ? whoIsAsking(b.whoIsAsking)
        : s.claims.length ? el("ul", {}, s.claims.map(claimItem))
        : el("p", { class: "caption" }, "Nothing here."))),
    unverified ? el("p", { class: "notice err" },
      `${unverified} claim${unverified === 1 ? "" : "s"} failed the citation check. Shown, but marked.`) : null,
    el("p", { class: "caption" }, "Code checks that cited requests and quotes exist; open a chip to judge whether it supports the claim."));
  return body;
}

function whoIsAsking(w) {
  const range = w.firstAt ? (formatDate(w.firstAt) === formatDate(w.lastAt) ? formatDate(w.firstAt)
    : `${formatDate(w.firstAt)} – ${formatDate(w.lastAt)}`) : "—";
  return el("ul", {},
    el("li", {}, `${w.featureRequests} Feature Request${w.featureRequests === 1 ? "" : "s"}, ${w.upvotes} Upvote${w.upvotes === 1 ? "" : "s"}`),
    el("li", {}, `Submitted ${range}`),
    el("li", {}, "Requesters picked: ", w.categoryPicks.map((c) => `${c.category} (${c.count})`).join(", ")));
}

function claimItem(c) {
  return el("li", {},
    c.text,
    c.quote ? el("q", { class: "quote" }, c.quote) : null,
    c.citations.map(citationChip),
    c.verified ? null : el("span", { class: "cite bad", title: c.problem ?? "" }, "⚠ unverified citation"));
}

function citationChip(c) {
  const moved = c.state === "moved";
  const label = `#${c.featureRequestId}${moved ? " ⚠ no longer on this Problem" : ""}`;
  return el("button", {
    class: `cite${c.state === "ok" ? "" : " bad"}`, type: "button",
    title: c.title ?? "No such Feature Request",
    onclick: () => openModal(c.title ?? `Feature Request #${c.featureRequestId}`, null,
      c.title ? el("p", { class: "original" }, c.description) : el("p", { class: "caption" }, "No Feature Request has this ID."),
      moved ? el("p", { class: "notice" }, "This request has moved to another Problem since the brief was written.") : null,
      c.state === "unverified" ? el("p", { class: "notice err" }, "This request isn't one of this Problem's Feature Requests.") : null,
      el("div", { class: "row" }, el("button", { class: "btn quiet", type: "button", onclick: closeModal }, "Close"))),
  }, label);
}

const DECISION_LABEL = { status: "Status", priority: "Priority", merge: "Merge", move: "Move", rename: "Rename", category: "Category" };

function decisionRow(d) {
  const x = d.details ?? {};
  const titles = (list) => (list ?? []).map((q) => `“${q.title}”`).join(", ");
  const n = (x.featureRequestIds ?? []).length;
  const requests = `${n} Feature Request${n === 1 ? "" : "s"}`;
  const what = d.kind === "merge" ? `Merged in ${titles(x.removed)} (${requests})`
    : d.kind === "move" ? `${requests} ${x.from?.length ? `from ${titles(x.from)}` : "that needed review"} to “${x.to?.title}”`
    : d.kind === "rename"
    ? (x.field === "description" ? "Description changed" : `“${x.from}” → “${x.to}”`)
    : ("from" in x || "to" in x) ? `${x.from ?? "Not set"} → ${x.to ?? "Not set"}` : "";
  const toClose = d.kind === "merge" ? x.issuesToClose ?? [] : [];
  return el("li", {},
    el("b", {}, DECISION_LABEL[d.kind] ?? d.kind), " ", what,
    d.reason ? el("div", { class: "caption" }, `Reason: ${d.reason}`) : null,
    toClose.length ? el("div", { class: "caption" }, "No longer linked, close by hand: ",
      toClose.map((i, n) => [n ? ", " : "", el("a", { class: "link", href: i.url, target: "_blank", rel: "noopener" }, `#${i.number}`)])) : null,
    el("div", { class: "caption" }, formatDate(d.createdAt)));
}

// ✦ Recommended Priority next to the team's Priority; the breakdown opens on demand.
function priorityBlock(p) {
  const rec = p.recommended;
  const breakdown = el("table", { class: "breakdown", hidden: true },
    el("tbody", {}, (rec?.breakdown ?? []).map((c) => el("tr", {},
      el("td", { class: "r" }, `${c.criterion} ${c.rating}/5`),
      el("td", {},
        c.reason,
        c.goal ? el("div", { class: "caption" }, `Goal: ${c.goal}`) : null,
        el("div", { class: "caption" },
          `weight ${c.weight} → ${formatPoints(c.points)} pts${c.criterion === "Reach" ? " · worked out by code" : " · rated by the AI"}`))))));
  const toggle = el("button", { class: "link", type: "button", style: "justify-self:start;font-size:13px" }, "Show how the score was worked out");
  toggle.addEventListener("click", () => {
    breakdown.hidden = !breakdown.hidden;
    toggle.textContent = `${breakdown.hidden ? "Show" : "Hide"} how the score was worked out`;
  });

  const retry = el("button", { class: "link", type: "button" }, "Retry");
  retry.addEventListener("click", async () => {
    retry.disabled = true;
    retry.textContent = "Scoring…";
    try {
      const fresh = await api(`/problems/${p.id}/retry-scoring`, { method: "POST" });
      document.querySelector(".drawer .prio")?.replaceWith(priorityBlock(fresh));
      toast(fresh.scoringFailed ? "Scoring failed again. See the AI call log." : "Recommended Priority updated.");
    } catch (e) {
      if (e instanceof SignedOut) return signIn();
      toast("Couldn't retry. Try again.");
      retry.disabled = false;
      retry.textContent = "Retry";
    }
  });

  const recommended = el("div", { class: "lv" },
    el("span", { class: "label" }, el("span", { class: "ai-mark" }, "✦ "), "Recommended Priority"),
    rec ? el("b", {}, rec.level, " ", el("span", { class: "caption" }, `${rec.score}/100`))
      : p.scoringFailed ? null : el("span", { class: "caption" }, "Not scored yet."),
    p.scoringFailed
      ? el("span", { class: "failed", role: "status" }, "Recommended Priority: scoring failed · ", retry)
      : null,
    p.scoringFailed && rec ? el("span", { class: "caption" }, "Showing the last score that worked.") : null);

  // The team's Priority. A level that differs from the recommended one asks for a one-line reason first.
  const reason = el("input", { type: "text", maxlength: "300", "aria-label": "Reason for the Priority" });
  const reasonError = el("span", { class: "caption", style: "color:var(--error)", hidden: true });
  const reasonForm = el("form", { class: "stack", style: "gap:4px", hidden: true },
    el("span", { class: "caption" }, "Your Priority differs from the Recommended Priority. One-line reason (goes in the decision log):"),
    reason, reasonError,
    el("div", { class: "row" }, el("button", { class: "btn sm" }, "Save Priority"),
      el("button", { class: "btn sm quiet", type: "button", onclick: () => { reasonForm.hidden = true; } }, "Cancel")));
  let pending = null;
  reasonForm.addEventListener("submit", async (e) => {
    e.preventDefault();
    reasonError.hidden = true;
    await save(p, { priority: pending, priorityReason: reason.value }, {
      done: `Priority: ${pending}.`, onErrors: (message) => { reasonError.textContent = message; reasonError.hidden = false; },
    });
  });
  const choose = async (level) => {
    if (level === p.priority) return;
    if (level && rec && level !== rec.level) {
      pending = level;
      reason.placeholder = `Why ${level} rather than ${rec.level}?`;
      reasonForm.hidden = false;
      reason.focus();
      return;
    }
    reasonForm.hidden = true;
    await save(p, { priority: level }, { done: level ? `Priority: ${level}.` : "Priority cleared." });
  };
  const buttons = el("div", { class: "row", style: "gap:6px;margin-top:4px", role: "group", "aria-label": "Priority" },
    ["High", "Medium", "Low"].map((l) => el("button", {
      class: `btn sm ${p.priority === l ? "" : "quiet"}`, type: "button", "aria-pressed": String(p.priority === l), onclick: () => choose(l),
    }, l)),
    p.priority ? el("button", { class: "link", type: "button", style: "font-size:12px", onclick: () => choose(null) }, "Clear") : null);
  const override = p.priority && p.priorityOverrideReason ? el("span", { class: "caption" }, `✎ ${p.priorityOverrideReason}`) : null;

  return el("div", { class: "prio" },
    el("div", { class: "levels" },
      recommended,
      el("div", { class: "lv" }, el("span", { class: "label" }, "Priority (yours)"), buttons, override)),
    reasonForm,
    rec ? toggle : null,
    rec ? breakdown : null);
}

const formatPoints = (n) => Number.isInteger(n) ? String(n) : Number(n).toFixed(2).replace(/0$/, "");

// onTick is null when the Problem is closed (its requests can't be moved).
function requestRow(r, onTick) {
  const tick = onTick ? el("input", {
    type: "checkbox", checked: ui.frSel.has(r.id), "aria-label": `Select ${r.title}`,
    onchange: (e) => { if (e.target.checked) ui.frSel.add(r.id); else ui.frSel.delete(r.id); onTick(); },
  }) : null;
  return el("div", { class: `fr${tick ? " tickable" : ""}` },
    tick,
    el("div", {},
      el("h4", {}, r.title),
      el("p", {}, r.description),
      el("div", { class: "caption" }, `${r.email} · ${formatDate(r.createdAt)} · Requester picked: ${r.category}`),
      el("div", { class: "why" }, el("b", {}, "✦ Why it's here: "), r.matchReason ?? "No reason recorded.",
        r.isBundled ? el("span", { class: "pill warn", style: "margin-left:6px" }, "Bundled") : null)),
    el("div", { class: "ups", "aria-label": `${r.upvotes} Upvotes` }, `▲ ${r.upvotes}`));
}

// ---------- Move (split) ----------
// The ticked Feature Requests go to a new Problem or another open one; a Problem left empty is removed.
function moveBarFor(p) {
  const n = p.featureRequests.filter((r) => ui.frSel.has(r.id)).length;
  if (!n) return el("p", { class: "caption" }, "Tick Feature Requests to split them into a new Problem or move them to another one.");
  const target = destinationPicker(p.category, p.id);
  const error = el("span", { class: "caption", style: "color:var(--error)", hidden: true });
  const button = el("button", { class: "btn sm", type: "button" }, "Move");
  button.addEventListener("click", async () => {
    error.hidden = true;
    button.disabled = true;
    const result = await moveRequests({ featureRequestIds: [...ui.frSel], ...target.body() });
    button.disabled = false;
    if (!result) return;
    if (result.errors) { error.textContent = firstError(result.errors); error.hidden = false; return; }
    ui.frSel.clear();
    const emptied = result.data.removedProblemIds.includes(p.id);
    toast(`Moved ${n} Feature Request${n === 1 ? "" : "s"}.${emptied ? " The emptied Problem was removed." : ""}`);
    if (emptied) location.hash = `#problems/${result.data.problemId}`; else await render();
  });
  return el("div", { class: "splitbar", role: "region", "aria-label": "Move selected Feature Requests" },
    el("b", {}, `${n} selected`), el("span", {}, "Move to"), target.node, button,
    el("button", { class: "btn sm quiet", type: "button", onclick: () => { ui.frSel.clear(); render(); } }, "Cancel"),
    error);
}

/** "A new Problem" (title + Category) or an existing open Problem; body() gives the move's destination fields. */
function destinationPicker(category, exceptId, title = "") {
  const to = el("select", { "aria-label": "Move to" }, el("option", { value: "new" }, "A new Problem"));
  const name = el("input", { type: "text", maxlength: "120", "aria-label": "New Problem title", placeholder: "New Problem title", value: title });
  const cat = el("select", { "aria-label": "New Problem Category" },
    CATEGORIES.map((c) => el("option", { value: c, selected: c === category }, c)));
  const fresh = el("span", { class: "row", style: "gap:6px" }, name, cat);
  to.addEventListener("change", () => { fresh.hidden = to.value !== "new"; });
  api("/problems").then((page) => to.append(...page.problems.filter((q) => q.id !== exceptId)
    .map((q) => el("option", { value: String(q.id) }, q.title)))).catch(() => {});
  return {
    node: el("span", { class: "row", style: "gap:6px" }, to, fresh),
    body: () => to.value === "new" ? { newProblem: { title: name.value, category: cat.value } } : { toProblemId: Number(to.value) },
  };
}

/** POST /feature-requests/move; null after a sign-out or network error (already reported). */
async function moveRequests(body) {
  try {
    return await post("/feature-requests/move", body);
  } catch (e) {
    if (e instanceof SignedOut) signIn(); else toast("Couldn't move. Try again.");
    return null;
  }
}

// ---------- Pop-ups (merge, needs review) ----------
let onModalClose = null;

function openModal(title, onClose, ...children) {
  closeModal();
  const box = el("div", { class: "modal", role: "dialog", "aria-modal": "true", "aria-label": title }, el("h3", {}, title), ...children);
  const scrim = el("div", { class: "scrim", onclick: (e) => { if (e.target === scrim) closeModal(); } }, box);
  document.body.append(scrim);
  onModalClose = onClose;
  box.querySelector("input,select,button")?.focus();
}

function closeModal() {
  const scrim = document.querySelector(".scrim");
  if (!scrim) return;
  scrim.remove();
  const then = onModalClose;
  onModalClose = null;
  then?.();
}

const cancelButton = () => el("button", { class: "btn quiet", type: "button", onclick: closeModal }, "Cancel");

// From the drawer: pick the other Problem, then the usual merge pop-up.
async function mergeWithModal(p) {
  let page;
  try { page = await api("/problems"); } catch (e) { if (e instanceof SignedOut) return signIn(); return toast("Couldn't load Problems."); }
  const others = page.problems.filter((q) => q.id !== p.id);
  if (!others.length) return toast("There's no other open Problem to merge with.");
  openModal(`Merge “${p.title}” with…`, null,
    el("div", { class: "stack", style: "gap:6px" }, others.map((q, i) => el("label", { class: "opt" },
      el("input", { type: "radio", name: "mergeWith", value: String(q.id), checked: i === 0 }),
      el("span", {}, el("b", {}, q.title), el("br"),
        el("span", { class: "caption" }, `${q.category} · ${q.featureRequests} Feature Requests · ${q.upvotes} Upvotes`))))),
    el("div", { class: "row" },
      el("button", { class: "btn", type: "button", onclick: () => {
        const picked = document.querySelector("input[name=mergeWith]:checked");
        if (picked) mergeModal([p.id, Number(picked.value)]);
      } }, "Next"),
      cancelButton()));
}

// Pick the Problem whose title, Category, status and Priority to keep; the pop-up lists what will happen.
async function mergeModal(ids) {
  let problems;
  try { problems = await Promise.all(ids.map((id) => api(`/problems/${id}`))); }
  catch (e) { if (e instanceof SignedOut) return signIn(); return toast("Couldn't load those Problems."); }
  const requests = problems.reduce((n, p) => n + p.featureRequests.length, 0);
  const upvotes = problems.reduce((n, p) => n + p.featureRequests.reduce((m, r) => m + r.upvotes, 0), 0);
  const others = problems.length - 1;
  const error = el("p", { class: "notice err", role: "alert", hidden: true });
  const go = el("button", { class: "btn", type: "button" }, "Merge");
  // One GitHub issue per Problem (as the server does it): the kept Problem's own, else the first other one's.
  const issues = el("li", {});
  const showIssues = () => {
    const keepId = Number(document.querySelector("input[name=keep]:checked")?.value ?? problems[0].id);
    const kept = problems.find((p) => p.id === keepId);
    const linked = problems.filter((p) => p.gitHubIssue);
    const stays = kept.gitHubIssue ? kept : linked.find((p) => p.id !== keepId);
    const toClose = linked.filter((p) => p !== stays);
    issues.replaceChildren(!linked.length ? "None of these Problems has a GitHub issue."
      : [`The kept Problem ${kept.gitHubIssue ? "keeps its" : "takes over"} GitHub issue #${stays.gitHubIssue.number}.`,
         toClose.length
           ? [" Close by hand: ", toClose.map((p, n) => [n ? ", " : "",
               el("a", { class: "link", href: p.gitHubIssue.url, target: "_blank", rel: "noopener" }, `#${p.gitHubIssue.number}`),
               ` (“${p.title}”)`]), ". They're also listed in the decision log."]
           : ""]);
  };
  go.addEventListener("click", async () => {
    const keepId = Number(document.querySelector("input[name=keep]:checked").value);
    go.disabled = true;
    error.hidden = true;
    try {
      const result = await post("/problems/merge", { problemIds: ids, keepId });
      if (result.errors) { error.textContent = firstError(result.errors); error.hidden = false; go.disabled = false; return; }
      ui.sel.clear();
      closeModal();
      toast(`Merged ${problems.length} Problems.`);
      if (location.hash === `#problems/${keepId}`) await render(); else location.hash = `#problems/${keepId}`;
    } catch (e) {
      if (e instanceof SignedOut) return signIn();
      error.textContent = "Couldn't merge. Try again.";
      error.hidden = false;
      go.disabled = false;
    }
  });
  openModal(`Merge ${problems.length} Problems`, null,
    el("div", { class: "stack", style: "gap:6px" },
      el("span", { class: "label" }, "Keep which title, Category, status and Priority?"),
      problems.map((p, i) => el("label", { class: "opt" },
        el("input", { type: "radio", name: "keep", value: String(p.id), checked: i === 0, onchange: showIssues }),
        el("span", {}, el("b", {}, p.title), el("br"), el("span", { class: "caption" },
          `${p.category} · ${p.status} · Priority ${p.priority ?? "not set"} · ${p.featureRequests.length} Feature Requests`))))),
    el("div", {}, el("span", { class: "label" }, "What happens"), el("ul", {},
      el("li", {}, `All ${requests} Feature Requests and their ${upvotes} Upvotes move to the kept Problem. Requesters notice nothing except more Related requests.`),
      el("li", {}, `The other Problem${others > 1 ? "s are" : " is"} removed (logged in the decision log, not the Archive).`),
      el("li", {}, "Reach is recounted and the AI re-scores Strategic fit, Pain and Retention risk."),
      el("li", {}, "Any Decision Brief becomes out of date."),
      issues)),
    error,
    el("div", { class: "row" }, go, cancelButton()));
  showIssues();
}

// "N Feature Requests need review": each with Retry AI or Match by hand (to an existing Problem or a new one).
async function needsReview() {
  let items;
  try { items = await api("/feature-requests/needs-review"); }
  catch (e) { if (e instanceof SignedOut) return signIn(); return toast("Couldn't load the list."); }
  let changed = false;
  const list = el("div", { class: "frs review-list" });
  const settled = (row, message) => {
    row.remove();
    changed = true;
    toast(message);
    if (!list.children.length) list.append(el("p", { class: "caption" }, "All matched."));
  };
  for (const r of items) list.append(reviewRow(r, settled));
  if (!items.length) list.append(el("p", { class: "caption" }, "Nothing needs review right now."));
  openModal("Feature Requests that need review", () => { if (changed) render(); },
    el("p", { class: "caption" }, "The AI couldn't match these to a Problem. Retry the AI, or match them by hand."),
    list,
    el("div", { class: "row" }, el("button", { class: "btn quiet", type: "button", onclick: closeModal }, "Done")));
}

function reviewRow(r, settled) {
  const row = el("div", { class: "fr review" });
  const failure = r.failure
    ? el("div", { class: "err-msg" }, `${r.failure.step} failed · ${r.failure.failureKind ?? "?"}${r.failure.errorMessage ? `: ${r.failure.errorMessage}` : ""}`)
    : el("div", { class: "caption" }, "No failed AI call logged: it may still be waiting in the queue.");
  const retry = el("button", { class: "btn sm", type: "button" }, "Retry AI");
  retry.addEventListener("click", async () => {
    retry.disabled = true;
    retry.textContent = "Matching…";
    try {
      const result = await post(`/feature-requests/${r.id}/retry-match`);
      if (result.data?.problemId) return settled(row, "Matched.");
      toast("The AI failed again. See the AI call log, or match it by hand.");
    } catch (e) {
      if (e instanceof SignedOut) return signIn();
      toast("Couldn't retry. Try again.");
    }
    retry.disabled = false;
    retry.textContent = "Retry AI";
  });
  const target = destinationPicker(r.category, null, r.title);
  const error = el("span", { class: "caption", style: "color:var(--error)", hidden: true });
  const match = el("button", { class: "btn sm quiet", type: "button" }, "Match by hand");
  match.addEventListener("click", async () => {
    error.hidden = true;
    match.disabled = true;
    const result = await moveRequests({ featureRequestIds: [r.id], ...target.body() });
    match.disabled = false;
    if (!result) return;
    if (result.errors) { error.textContent = firstError(result.errors); error.hidden = false; return; }
    settled(row, "Matched by hand.");
  });
  row.append(el("div", { class: "stack", style: "gap:6px" },
    el("div", {}, el("h4", {}, r.title), el("p", {}, r.description),
      el("div", { class: "caption" }, `${r.email} · ${formatDate(r.createdAt)} · Requester picked: ${r.category} · ▲ ${r.upvotes}`),
      failure),
    el("div", { class: "row", style: "gap:6px" }, retry, el("span", { class: "caption" }, "or match by hand to"), target.node, match),
    error));
  return row;
}

async function loadCalls(node, problemId) {
  const list = await api(`/ai-calls?problemId=${problemId}`);
  node.dataset.loaded = "1";
  node.replaceChildren(
    list.length ? callsTable(list, { showFor: false }) : el("p", { class: "caption" }, "No AI calls for this Problem yet."),
    el("p", { style: "margin-top:8px" }, el("a", { class: "link", href: `#settings/ai-calls?problem=${problemId}` }, "Open in the AI call log")));
}

// ---------- Settings → AI call log ----------
async function settingsView(params) {
  const status = params.get("status") === "Failed" ? "Failed" : "";
  const problem = params.get("problem") ?? "";
  const query = new URLSearchParams();
  if (status) query.set("status", status);
  if (problem) query.set("problemId", problem);
  const [calls, page] = await Promise.all([api(`/ai-calls?${query}`), api("/problems")]);

  const go = (next) => {
    const q = new URLSearchParams();
    if (next.status) q.set("status", next.status);
    if (next.problem) q.set("problem", next.problem);
    location.hash = `#settings/ai-calls${q.size ? `?${q}` : ""}`;
  };
  const statusTabs = el("div", { class: "tabs", role: "group", "aria-label": "Result" },
    [["", "All"], ["Failed", "Failed"]].map(([v, label]) => el("button", {
      class: "tab", type: "button", "aria-selected": String(status === v), onclick: () => go({ status: v, problem }),
    }, label)));
  const problemPick = el("select", { "aria-label": "Problem", onchange: (e) => go({ status, problem: e.target.value }) },
    el("option", { value: "" }, "All Problems"),
    page.problems.map((p) => el("option", { value: String(p.id), selected: String(p.id) === problem }, p.title)));
  if (problem && !page.problems.some((p) => String(p.id) === problem))
    problemPick.append(el("option", { value: problem, selected: true }, `Problem ${problem}`));

  app.replaceChildren(el("div", { class: "stack reveal" },
    settingsTabs("ai-calls"),
    el("div", {}, el("h2", {}, "AI call log"),
      el("p", { class: "muted" }, "Every AI call, with its result, model, tokens, cost and what it was for.")),
    el("div", { class: "spread" }, statusTabs, problemPick),
    calls.length ? el("div", { class: "table-wrap" }, callsTable(calls, { showFor: true }))
      : el("div", { class: "empty" }, el("span", { class: "sketch sk-cup", "aria-hidden": "true" }), "No AI calls match.")));
}

const SETTINGS_TABS = [["ai-calls", "AI call log"], ["criteria", "Priority criteria"], ["github", "GitHub"], ["archive", "Archive"]];

// ---------- Settings → GitHub ----------
async function githubView() {
  const box = el("div", { class: "box stack", style: "gap:8px" });
  const show = (g) => {
    const check = el("button", { class: "btn sm", type: "button" }, "Check again");
    check.addEventListener("click", async () => {
      check.disabled = true;
      try { show(await api("/github/check", { method: "POST" })); }
      catch (e) { if (e instanceof SignedOut) return signIn(); toast("Couldn't check. Try again."); check.disabled = false; }
    });
    const when = new Date(g.checkedAt).toLocaleString("en-GB", { day: "numeric", month: "short", hour: "2-digit", minute: "2-digit" });
    box.replaceChildren(
      el("div", {}, el("span", { class: "label" }, "Repo"), " ",
        g.repo ? el("a", { class: "link", href: `https://github.com/${g.repo}`, target: "_blank", rel: "noopener" }, g.repo)
          : el("span", { class: "mono" }, g.configuredRepo || "Not set"),
        g.repo && g.repo !== g.configuredRepo ? el("div", { class: "caption" }, `Configured as ${g.configuredRepo}; GitHub now calls it ${g.repo}.`) : null,
        g.defaultBranch ? el("div", { class: "caption" }, `Default branch: ${g.defaultBranch}`) : null),
      el("div", {}, el("span", { class: "label" }, "Token"), " ",
        g.ok ? el("span", { class: "pill Low" }, "OK") : el("span", { class: "pill warn" }, "Failing"),
        g.error ? el("div", { class: "err-msg" }, g.error) : null,
        el("div", { class: "caption" }, `Checked ${when}`)),
      el("div", {}, check));
  };
  show(await api("/github"));
  app.replaceChildren(el("div", { class: "stack reveal" },
    settingsTabs("github"),
    el("div", {}, el("h2", {}, "GitHub"),
      el("p", { class: "muted" }, "The repo engineering works in, set in the config file. The token is a server secret.")),
    box));
}

// ---------- Settings → Archive ----------
async function archiveView() {
  const rows = await api("/archive");
  const frozen = (r) => r
    ? el("span", { class: `pill ${r.level}`, title: `Recommended Priority ${r.score}/100` }, `✦ ${r.level} · ${r.score}`)
    : el("span", { class: "caption" }, "Never scored");
  app.replaceChildren(el("div", { class: "stack reveal" },
    settingsTabs("archive"),
    el("div", {}, el("h2", {}, "Archive"),
      el("p", { class: "muted" }, "Problems closed as Shipped or Won't do. Their Recommended Priority is frozen at closing.")),
    rows.length ? el("div", { class: "table-wrap" }, el("table", { class: "tbl" },
      el("thead", {}, el("tr", {}, ["Problem", "Closed as", "When", "Recommended (frozen)", "Priority", "Reason"].map((h) => el("th", {}, h)))),
      el("tbody", {}, rows.map((r) => el("tr", {},
        el("td", {}, el("a", { class: "link", href: `#problems/${r.id}` }, r.title),
          el("div", { class: "caption" }, `${r.category} · ${r.featureRequests} Feature Requests · ${r.upvotes} Upvotes`)),
        el("td", {}, r.status),
        el("td", { class: "mono" }, formatDate(r.closedAt)),
        el("td", {}, frozen(r.recommended)),
        el("td", {}, priorityPill(r.priority)),
        el("td", { class: "caption" }, r.reason ?? ""))))))
      : el("div", { class: "empty" }, el("span", { class: "sketch sk-cup", "aria-hidden": "true" }), "Nothing closed yet.")));
}

function settingsTabs(current) {
  return el("div", { class: "tabs", role: "tablist", "aria-label": "Settings" },
    SETTINGS_TABS.map(([key, label]) => el("button", {
      class: "tab", role: "tab", type: "button", "aria-selected": String(key === current),
      onclick: () => { location.hash = `#settings/${key}`; },
    }, label)));
}

// ---------- Settings → Priority criteria (read-only; set once in the config file) ----------
async function criteriaView() {
  const c = await api("/criteria");
  const rated = c.criteria.filter((x) => x.levels);
  const bands = c.reachBands.map((b, i) => {
    const next = c.reachBands[i + 1];
    const range = next ? (b.minPoints === 0 ? `under ${next.minPoints}` : `${b.minPoints}–${next.minPoints - 1}`) : `${b.minPoints}+`;
    return `${range} → ${b.rating}`;
  }).join(", ");

  app.replaceChildren(el("div", { class: "stack criteria reveal" },
    settingsTabs("criteria"),
    el("div", {}, el("h2", {}, "Priority criteria"),
      el("p", { class: "muted" }, "Set once in the repo's config file and left alone; shown here so you can see how every Recommended Priority is worked out. Read-only.")),
    el("div", { class: "box" },
      el("span", { class: "label" }, "Studease product goals (for Strategic fit)"),
      el("ol", {}, c.goals.map((g) => el("li", {}, g)))),
    el("div", { class: "box table-wrap" },
      el("table", { class: "tbl" },
        el("thead", {}, el("tr", {}, ["Criterion", "Weight", "Rated by"].map((h) => el("th", {}, h)))),
        el("tbody", {}, c.criteria.map((x) => el("tr", {},
          el("td", {}, x.name), el("td", { class: "mono" }, String(x.weight)),
          el("td", {}, x.ratedBy, x.levels ? null : el("div", { class: "caption" }, `Reach points in bands: ${bands}`)))))),
      el("p", { class: "caption", style: "margin-top:8px" },
        `Score = Σ weight × (rating − 1) / 4, so 0–100. Levels: High ≥ ${c.cutOffs.high}, Medium ${c.cutOffs.medium}–${c.cutOffs.high - 1}, Low < ${c.cutOffs.medium}.`)),
    el("div", { class: "box table-wrap" },
      el("table", { class: "tbl" },
        el("thead", {}, el("tr", {}, el("th", {}, "Rating"), rated.map((x) => el("th", {}, x.name)))),
        el("tbody", {}, [0, 1, 2, 3, 4].map((i) => el("tr", {},
          el("td", {}, el("b", {}, String(i + 1))), rated.map((x) => el("td", {}, x.levels[i] ?? "")))))),
      el("p", { class: "caption", style: "margin-top:8px" }, `A core task means ${c.coreTasks}.`))));
}

function callsTable(list, { showFor }) {
  const when = (iso) => new Date(iso).toLocaleString("en-GB", { day: "numeric", month: "short", hour: "2-digit", minute: "2-digit" });
  const cost = (c) => c.costUsd == null ? "–" : `$${Number(c.costUsd).toFixed(c.costUsd < 0.01 ? 5 : 3)}`;
  const tokens = (c) => c.inputTokens == null ? "–" : `${c.inputTokens} / ${c.outputTokens}`;
  const forCell = (c) => el("td", {},
    c.problemId ? el("a", { class: "link", href: `#problems/${c.problemId}` }, `Problem ${c.problemId}`) : null,
    c.problemId && c.featureRequestId ? " · " : null,
    c.featureRequestId ? el("a", { class: "link", href: `/requests/${c.featureRequestId}`, target: "_blank", rel: "noopener" }, `Request ${c.featureRequestId}`) : null);
  return el("table", { class: "tbl" },
    el("thead", {}, el("tr", {}, ["When", "Step", "Result", ...(showFor ? ["For"] : []), "Model", "Tokens in / out", "Cost", "Time"].map((h) => el("th", {}, h)))),
    el("tbody", {}, list.map((c) => el("tr", {},
      el("td", { class: "mono" }, when(c.createdAt)),
      el("td", {}, c.step),
      el("td", {},
        c.status === "OK" ? el("span", { class: "pill Low" }, "OK") : el("span", { class: "pill warn" }, `Failed · ${c.failureKind ?? "?"}`),
        c.errorMessage ? el("div", { class: "err-msg" }, c.errorMessage) : null,
        c.note ? el("div", { class: "caption" }, c.note) : null),
      showFor ? forCell(c) : null,
      el("td", { class: "caption" }, c.model ?? "–"),
      el("td", { class: "mono" }, tokens(c)),
      el("td", { class: "mono" }, cost(c)),
      el("td", { class: "mono" }, `${(c.latencyMs / 1000).toFixed(1)} s`)))));
}

// ---------- Outbox: Drafts / Sent on the left, the chosen message on the right ----------
const OUTBOX_STATE = { Drafting: ["", "✦ Drafting…"], "Draft failed": ["warn", "Draft failed"], Ready: ["Medium", "✦ AI draft"], Sent: ["Low", "Sent"] };

async function outboxView(tab) {
  const [drafts, sent] = await Promise.all([api("/outbox?tab=drafts"), api("/outbox?tab=sent")]);
  const list = tab === "sent" ? sent : drafts;
  if (!list.some((m) => m.id === ui.outboxId)) ui.outboxId = list[0]?.id ?? null;
  const chosen = list.find((m) => m.id === ui.outboxId);

  const tabLink = (key, label, count) => el("a", {
    class: "tab", href: key === "sent" ? "#outbox/sent" : "#outbox", role: "tab", "aria-selected": String(tab === key),
  }, `${label} · ${count}`);
  const item = (m) => el("button", {
    type: "button", class: "ob-item", "aria-current": m.id === ui.outboxId ? "true" : null,
    onclick: () => { ui.outboxId = m.id; render(); },
  },
    el("span", { class: "spread" }, el("b", {}, m.toEmail), statePill(m.status)),
    el("span", { class: "caption" }, m.subject || (m.status === "Draft failed" ? "No draft yet" : "…")),
    el("span", { class: "caption" }, `${m.problemTitle ?? "Problem removed"} · ${formatDate(m.sentAt ?? m.createdAt)}`));

  app.replaceChildren(el("div", { class: "stack reveal" },
    el("div", {}, el("h2", {}, "Outbox"),
      el("p", { class: "muted" }, "When a Problem ships, the AI drafts one update per Requester. Nothing goes out until you approve it. (Demo: approved updates move to Sent; no email is really delivered.)")),
    el("div", { class: "outbox" },
      el("aside", { class: "ob-list" },
        el("div", { class: "tabs", role: "tablist", "aria-label": "Outbox" }, tabLink("drafts", "Drafts", drafts.length), tabLink("sent", "Sent", sent.length)),
        list.length ? list.map(item)
          : el("div", { class: "empty" }, el("span", { class: "sketch sk-cup", "aria-hidden": "true" }),
            tab === "sent" ? "Nothing sent yet." : "Nothing waiting to go out.")),
      chosen ? messagePane(chosen) : null)));
}

function statePill(status) {
  const [cls, label] = OUTBOX_STATE[status] ?? ["", status];
  return el("span", { class: `pill ${cls}` }, label);
}

function messagePane(m, editing = false) {
  const pane = el("section", { class: "ob-pane box", "aria-label": `Update to ${m.toEmail}` });
  const act = async (path, method, done) => {
    try {
      await api(`/outbox/${m.id}${path}`, { method });
      if (done) toast(done);
    } catch (e) {
      if (e instanceof SignedOut) return signIn();
      toast("That didn't work. Try again.");
    }
    render();
  };
  const head = el("div", { class: "stack", style: "gap:4px" },
    el("div", { class: "spread" }, el("span", {}, "To ", el("b", {}, m.toEmail)), statePill(m.status)),
    el("span", { class: "caption" }, "About ",
      m.problemId ? el("a", { class: "link", href: `#problems/${m.problemId}` }, m.problemTitle ?? `Problem ${m.problemId}`) : "a removed Problem",
      " · their requests: ", m.featureRequests.map((r, i) => [i ? ", " : "",
        el("a", { class: "link", href: `/requests/${r.id}`, target: "_blank", rel: "noopener" }, `“${r.title}”`)])));

  if (editing) {
    const subject = el("input", { type: "text", value: m.subject, "aria-label": "Subject", maxlength: "200", required: true });
    const body = el("textarea", { rows: "10", "aria-label": "Message", maxlength: "5000", required: true });
    body.value = m.body;
    const error = el("p", { class: "notice err", role: "alert", hidden: true });
    const form = el("form", { class: "stack", style: "gap:10px" },
      subject, body, error,
      el("div", { class: "row" }, el("button", { class: "btn sm" }, "Save"),
        el("button", { class: "btn sm quiet", type: "button", onclick: () => pane.replaceWith(messagePane(m)) }, "Cancel")));
    form.addEventListener("submit", async (e) => {
      e.preventDefault();
      const res = await fetch(`/api/admin/outbox/${m.id}`, {
        method: "PATCH", headers: { "Content-Type": "application/json" }, credentials: "same-origin",
        body: JSON.stringify({ subject: subject.value, body: body.value }),
      });
      if (res.status === 401) return signIn();
      if (res.status === 400) {
        error.textContent = Object.values((await res.json()).errors ?? {}).flat()[0] ?? "That wasn't saved.";
        error.hidden = false;
        return;
      }
      if (!res.ok) { toast("Couldn't save. Try again."); return render(); }
      toast("Saved.");
      render();
    });
    pane.append(head, form);
    setTimeout(() => subject.focus());
    return pane;
  }

  const sent = m.status === "Sent";
  const failed = m.status === "Draft failed" || m.status === "Drafting";
  pane.append(head,
    failed
      ? el("p", { class: "failed" }, m.status === "Drafting" ? "✦ Still drafting. If it's stuck, retry. " : "Draft failed · ",
        el("button", { class: "link", type: "button", onclick: () => act("/retry", "POST", null) }, "Retry"),
        el("span", { class: "caption" }, " or write it yourself with Edit."))
      : null,
    m.subject ? el("div", { class: "ob-mail" }, el("b", {}, m.subject), el("p", {}, m.body)) : null,
    sent
      ? el("p", { class: "caption" }, `Approved and marked Sent ${formatDate(m.sentAt)}.`)
      : el("div", { class: "row" },
        el("button", { class: "btn sm", type: "button", disabled: m.status !== "Ready", onclick: () => act("/send", "POST", "Approved: moved to Sent.") }, "Approve"),
        el("button", { class: "btn sm quiet", type: "button", onclick: () => pane.replaceWith(messagePane(m, true)) }, "Edit"),
        el("button", { class: "btn sm quiet", type: "button", onclick: () => act("", "DELETE", "Draft discarded.") }, "Discard")));
  return pane;
}

// ---------- metrics ----------
// Seed metrics are saved by `seed load`; the priority consistency check runs from its own box.
async function metricsView() {
  const m = await api("/metrics");
  const s = m.seed;
  const pct = (x) => (x == null ? "n/a" : `${Math.round(x * 100)}%`);
  const tile = (name, value, target, what) => el("div", { class: "box metric" },
    el("span", { class: "label" }, name),
    el("div", { class: "metric-value" }, pct(value)),
    el("div", { class: "row" },
      el("span", { class: `pill ${value != null && value >= target ? "met" : "missed"}` },
        value != null && value >= target ? "On target" : "Below target"),
      el("span", { class: "caption" }, `Target ≥${pct(target)}`)),
    el("p", { class: "caption" }, what));

  app.replaceChildren(el("div", { class: "stack reveal" },
    el("div", {}, el("h2", {}, "Metrics"),
      el("p", { class: "muted" }, s
        ? `How well the AI grouped the synthetic seed backlog: its first answers, measured at seed load, ${formatDate(s.computedAt)}. Merges and moves since then don't change these numbers.`
        : "Nothing measured yet. Run `seed load` to send the synthetic backlog through the AI and measure it.")),
    s ? el("div", { class: "metrics" },
      tile("Duplicate catch rate", s.duplicateCatchRate, m.targets.duplicateCatchRate,
        "Of the requests written for a need that already had a Problem, the share the AI linked to an existing Problem instead of making a new one."),
      tile("Grouping accuracy", s.groupingAccuracy, m.targets.groupingAccuracy,
        "Share of all requests linked to the right Problem. Each AI Problem stands for the need most of its requests were written for."),
      el("div", { class: "box metric" },
        el("span", { class: "label" }, "Grouping"),
        el("div", { class: "metric-value" }, String(s.aiProblemCount)),
        el("p", {}, `AI made ${s.aiProblemCount} Problems where the data has ${s.intendedProblemCount}.`),
        el("p", { class: "caption" }, s.failedMatches === 0 ? "No failed matches."
          : `${s.failedMatches} failed ${s.failedMatches === 1 ? "match" : "matches"} (counted as wrong).`)))
      : el("div", { class: "empty" }, el("span", { class: "sketch sk-cup", "aria-hidden": "true" }), "No seed metrics yet."),
    consistencyBox(m.consistency, m.targets.priorityConsistency, pct)));
}

// The last consistency check (POST /metrics/consistency-check re-scores every open Problem 3 times; saved scores never change).
function consistencyBox(c, target, pct) {
  const button = el("button", { class: "btn sm", type: "button" }, c ? "Run again" : "Run consistency check");
  button.onclick = async () => {
    button.disabled = true;
    button.textContent = "Re-scoring every open Problem 3 times…";
    try {
      await post("/metrics/consistency-check");
      await metricsView();
      toast("Consistency check finished.");
    } catch (e) {
      if (e instanceof SignedOut) return signIn();
      button.disabled = false;
      button.textContent = "Run consistency check";
      toast("The consistency check didn't finish. Try again.");
    }
  };
  const met = c?.sameLevelShare != null && c.sameLevelShare >= target;
  const plural = (n, one, many) => `${n} ${n === 1 ? one : many}`;
  return el("div", { class: "box metric", id: "consistency" },
    el("span", { class: "label" }, "Priority consistency"),
    c ? el("div", { class: "metric-value" }, pct(c.sameLevelShare)) : null,
    c ? el("div", { class: "row" },
      el("span", { class: `pill ${met ? "met" : "missed"}` }, met ? "On target" : "Below target"),
      el("span", { class: "caption" }, `Target ≥${pct(target)}`)) : null,
    el("p", { class: "caption" }, c
      ? `Share of ${plural(c.checked, "open Problem", "open Problems")} that kept the same level on all 3 re-scores. Ran ${formatDate(c.ranAt)}.`
      : "Re-scores every open Problem 3 times against the Priority Criteria and shows how often the level holds. Saved scores never change."),
    c && c.averageSpread != null ? el("p", {}, `Average score spread: ${c.averageSpread.toFixed(1)} points (of 100).`) : null,
    c && c.couldntCheck > 0 ? el("p", {}, `Couldn't check: ${plural(c.couldntCheck, "Problem", "Problems")} (a re-score failed; see the AI call log).`) : null,
    c && c.changed.length ? el("div", {},
      el("p", {}, "Changed level:"),
      el("ul", {}, ...c.changed.map((p) => el("li", {},
        el("a", { href: `#problems/${p.problemId}` }, p.title),
        el("span", { class: "caption" }, ` ${p.levels.join(" → ")} (${p.scores.join(", ")})`))))) : null,
    el("div", {}, button));
}

document.addEventListener("keydown", (e) => {
  if (e.key !== "Escape") return;
  if (document.querySelector(".scrim")) closeModal();
  else if (document.querySelector(".drawer")) location.hash = "#problems";
});
window.addEventListener("hashchange", render);
render();
