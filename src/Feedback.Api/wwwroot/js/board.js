import { CATEGORIES, apiFetch, el, formatDate, setUpRequestForm, upvoteButton } from "./portal.js";

const params = new URLSearchParams(location.search);
const state = { category: params.get("category") ?? "", sort: params.get("sort") === "new" ? "new" : "top" };
const sorts = document.getElementById("sorts");
const cats = document.getElementById("cats");
const list = document.getElementById("list");
const count = document.getElementById("count");

function renderTabs() {
  cats.replaceChildren(...["", ...CATEGORIES].map((c) => el("button", {
    type: "button", class: "tab", role: "tab", "aria-selected": String(state.category === c),
    onclick: () => { state.category = c; syncUrl(); renderTabs(); load(); },
  }, c || "All")));
}

function renderSorts() {
  sorts.replaceChildren(...[["top", "Most upvoted"], ["new", "Newest"]].map(([value, label]) => el("button", {
    type: "button", class: "tab", "aria-pressed": String(state.sort === value),
    onclick: () => { state.sort = value; syncUrl(); renderSorts(); load(); },
  }, label)));
}

function syncUrl() {
  const q = new URLSearchParams();
  if (state.category) q.set("category", state.category);
  if (state.sort === "new") q.set("sort", "new");
  const qs = q.toString();
  history.replaceState(null, "", location.pathname + (qs ? `?${qs}` : ""));
}

async function load() {
  const q = new URLSearchParams({ category: state.category, sort: state.sort });
  let rows;
  try {
    const res = await apiFetch(`/api/requests?${q}`);
    if (!res.ok) throw new Error(res.status);
    rows = await res.json();
  } catch {
    count.textContent = "";
    list.replaceChildren(el("div", { class: "empty" }, "We couldn't load the requests. Please refresh to try again."));
    return;
  }

  count.textContent = `${rows.length} ${rows.length === 1 ? "request" : "requests"}`;
  if (rows.length === 0) {
    list.replaceChildren(el("div", { class: "empty" },
      el("span", { class: "sketch sk-cat", "aria-hidden": "true" }), "Nothing here yet. Be the first to ask."));
    return;
  }
  list.replaceChildren(el("table", { class: "board" }, el("tbody", {}, rows.map(row))));
}

function row(r) {
  const href = `/requests/${r.id}`;
  return el("tr", { onclick: (e) => { if (!e.target.closest("a, button")) location.href = href; } },
    el("td", { class: "u" }, upvoteButton(r)),
    el("td", { class: "t" },
      el("a", { class: "rowlink", href }, r.title),
      el("span", { class: "caption" }, r.description)),
    el("td", { class: "c" },
      el("span", { class: "cat" }, r.category),
      el("span", { class: "caption", style: "display:block" }, formatDate(r.createdAt))));
}

setUpRequestForm(() => load());
renderTabs();
renderSorts();
load();
