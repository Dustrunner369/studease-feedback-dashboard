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
      el("p", {}, r.description))));
}

function missing(message) {
  main.replaceChildren(el("div", { class: "empty" },
    el("span", { class: "sketch sk-cat", "aria-hidden": "true" }), message));
}

setUpRequestForm();
load();
