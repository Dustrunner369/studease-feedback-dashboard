import { CATEGORIES, el, formatDate, setUpRequestForm } from "./portal.js";

const state = { category: new URLSearchParams(location.search).get("category") ?? "" };
const cats = document.getElementById("cats");
const list = document.getElementById("list");
const count = document.getElementById("count");

function renderTabs() {
  cats.replaceChildren(...["", ...CATEGORIES].map((c) => el("button", {
    type: "button", class: "tab", role: "tab", "aria-selected": String(state.category === c),
    onclick: () => { state.category = c; syncUrl(); renderTabs(); load(); },
  }, c || "All")));
}

function syncUrl() {
  const q = state.category ? `?category=${encodeURIComponent(state.category)}` : "";
  history.replaceState(null, "", location.pathname + q);
}

async function load() {
  const q = new URLSearchParams({ category: state.category, sort: "new" });
  let rows;
  try {
    const res = await fetch(`/api/requests?${q}`);
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
  return el("tr", { onclick: (e) => { if (!e.target.closest("a")) location.href = href; } },
    el("td", { class: "t" },
      el("a", { class: "rowlink", href }, r.title),
      el("span", { class: "caption" }, r.description)),
    el("td", { class: "c" },
      el("span", { class: "cat" }, r.category),
      el("span", { class: "caption", style: "display:block" }, formatDate(r.createdAt))));
}

setUpRequestForm(() => load());
renderTabs();
load();
