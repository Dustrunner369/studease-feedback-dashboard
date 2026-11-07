import { apiFetch, el, formatDate, setUpRequestForm, upvoteButton } from "./portal.js";

const main = document.getElementById("request");
const id = location.pathname.split("/").pop();

async function load() {
  let r;
  try {
    const res = await apiFetch(`/api/requests/${encodeURIComponent(id)}`);
    if (res.status === 404) return missing("We couldn't find that request.");
    if (!res.ok) throw new Error(res.status);
    r = await res.json();
  } catch {
    return missing("We couldn't load this request. Please refresh to try again.");
  }

  document.title = `${r.title} · Studease`;
  main.replaceChildren(el("article", { class: "fr-detail" },
    upvoteButton(r),
    el("div", {},
      el("span", { class: "cat" }, r.category), " ",
      el("span", { class: "caption" }, `· ${formatDate(r.createdAt)}`),
      el("h1", {}, r.title),
      el("p", {}, r.description))),
    related(r.related ?? []));
}

// The other Feature Requests of the same Problem. Empty until the request has been matched.
function related(rows) {
  if (rows.length === 0) return "";
  return el("section", { class: "related" },
    el("h2", {}, "Related requests"),
    el("table", { class: "board" }, el("tbody", {}, rows.map(relatedRow))));
}

function relatedRow(r) {
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

function missing(message) {
  main.replaceChildren(el("div", { class: "empty" },
    el("span", { class: "sketch sk-cat", "aria-hidden": "true" }), message));
}

setUpRequestForm();
load();
