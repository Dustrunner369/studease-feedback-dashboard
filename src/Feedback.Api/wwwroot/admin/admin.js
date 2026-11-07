// Studease /admin. Hash routes: #problems[/<id>], #outbox, #metrics, #settings/ai-calls[?status=Failed&problem=<id>].
// All user and AI text goes into the DOM through textContent (via el), never innerHTML.
import { el, CATEGORIES, toast, formatDate } from "/js/portal.js";

const STATUSES = ["New", "Under review", "Planned", "In progress", "Shipped", "Won't do"];
const PRIORITY_RANK = { High: 0, Medium: 1, Low: 2 };
const app = document.getElementById("app");
const top = document.getElementById("top");

const ui = { tab: "All", sort: null, dir: 1 };

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
  try {
    if (r.view === "problems") await problemsView(r.sub ? Number(r.sub) : null);
    else if (r.view === "outbox") placeholder("Outbox", "AI-drafted updates for Requesters appear here when a Problem ships. Coming in a later slice.");
    else if (r.view === "metrics") placeholder("Metrics", "Duplicate catch rate, grouping accuracy and priority consistency appear here. Coming in a later slice.");
    else if (r.view === "settings") await settingsView(r.params);
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

  const table = rows.length
    ? el("div", { class: "table-wrap" }, el("table", { class: "pt" },
        el("thead", {}, el("tr", {}, th("title", "Problem"), th("status", "Status"), th("requests", "Requests", "num"), th("priority", "Priority"))),
        el("tbody", {}, rows.map((p) => el("tr", { class: p.id === openId ? "open" : null },
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
        `${page.needsReview} Feature Request${page.needsReview === 1 ? "" : "s"} need${page.needsReview === 1 ? "s" : ""} review: the AI couldn't match ${page.needsReview === 1 ? "it" : "them"}.`)
    : null;

  app.replaceChildren(el("div", { class: "reveal" }, review, el("div", { class: "toolbar" }, tabs), table));
  document.querySelector(".drawer")?.remove();
  if (openId) await drawer(openId);
}

function sortBy(key) {
  if (ui.sort === key) ui.dir = -ui.dir;
  else { ui.sort = key; ui.dir = key === "requests" ? -1 : 1; }
  render();
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

  const calls = el("div", {}, el("p", { class: "caption" }, "Loading…"));
  const aside = el("aside", { class: "drawer", "aria-label": `Problem: ${p.title}` },
    el("div", { class: "spread" }, el("span", { class: "label" }, "Problem"),
      el("button", { class: "btn sm quiet", type: "button", onclick: close }, "Close ✕")),
    el("h2", {}, p.title),
    el("p", { class: "muted" }, p.description),
    el("div", { class: "meta" },
      el("div", {}, el("span", { class: "label" }, "Status"), el("span", {}, p.status)),
      el("div", {}, el("span", { class: "label" }, "Category"), el("span", {}, p.category)),
      el("div", {}, el("span", { class: "label" }, "Priority"), priorityPill(p.priority))),
    el("details", { open: true },
      el("summary", {}, `Feature Requests (${p.featureRequests.length})`),
      el("div", { class: "frs" }, p.featureRequests.map(requestRow))),
    el("details", { ontoggle: (e) => { if (e.target.open && !calls.dataset.loaded) loadCalls(calls, p.id); } },
      el("summary", {}, "AI calls"), calls));
  document.querySelector(".drawer")?.remove();
  document.body.append(aside);
  aside.querySelector("button").focus();
}

function requestRow(r) {
  return el("div", { class: "fr" },
    el("div", {},
      el("h4", {}, r.title),
      el("p", {}, r.description),
      el("div", { class: "caption" }, `${r.email} · ${formatDate(r.createdAt)} · Requester picked: ${r.category}`),
      el("div", { class: "why" }, el("b", {}, "✦ Why it's here: "), r.matchReason ?? "No reason recorded.",
        r.isBundled ? el("span", { class: "pill warn", style: "margin-left:6px" }, "Bundled") : null)),
    el("div", { class: "ups", "aria-label": `${r.upvotes} Upvotes` }, `▲ ${r.upvotes}`));
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
    el("div", { class: "tabs", role: "tablist", "aria-label": "Settings" },
      el("button", { class: "tab", role: "tab", type: "button", "aria-selected": "true" }, "AI call log")),
    el("div", {}, el("h2", {}, "AI call log"),
      el("p", { class: "muted" }, "Every AI call, with its result, model, tokens, cost and what it was for.")),
    el("div", { class: "spread" }, statusTabs, problemPick),
    calls.length ? el("div", { class: "table-wrap" }, callsTable(calls, { showFor: true }))
      : el("div", { class: "empty" }, el("span", { class: "sketch sk-cup", "aria-hidden": "true" }), "No AI calls match.")));
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

function placeholder(title, text) {
  app.replaceChildren(el("div", { class: "stack reveal" }, el("h2", {}, title),
    el("div", { class: "empty" }, el("span", { class: "sketch sk-cup", "aria-hidden": "true" }), text)));
}

document.addEventListener("keydown", (e) => {
  if (e.key === "Escape" && document.querySelector(".drawer")) location.hash = "#problems";
});
window.addEventListener("hashchange", render);
render();
