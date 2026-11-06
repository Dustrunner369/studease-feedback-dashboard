# How Suggest Solutions reads the connected repo

Research for issue #6 (child of map #1). Researched 2026-11-05.

**Question:** How should Suggest Solutions find the code and open issues/PRs in the connected GitHub repo that are relevant to a Problem, using a fine-grained personal access token (PAT) scoped to one repo, and fit them into one AI call's context?

This note recommends an approach. It does not make the decision.

## TL;DR

- **Recommended approach: tree first, then the LLM picks the files.** For each click, fetch the repo's recursive file tree (1 request), drop the noise (images, platform scaffolding, generated migrations), and send the path list, the Problem and all open issues/PRs to the model in a short first step that picks up to about 8 files. Fetch those files raw (one request each) and make the main Suggest Solutions call with their contents. That is 2 LLM calls per click, or 1 if the whole filtered tree plus the repo's `context/*.md` docs are sent as fixed context. About 10 GitHub requests in total, all well inside rate limits.
- **Open issues and PRs:** just list them (`GET /repos/{o}/{r}/issues?state=open`, which returns PRs as well). The demo repo has **0 open issues and 0 open PRs** today, so searching them adds nothing.
- **Code search (`search/code`)** works with a fine-grained PAT and needs no permission, but it is keyword-only. It returns paths and short fragments, not whole files, is limited to 10 requests/min, and only covers the default branch. It is a useful optional extra, not the backbone.
- **Token permissions:** Contents: read, Issues: read (read/write if the app also files issues), Pull requests: read, and Metadata: read (added automatically). Search needs no extra permission.
- **Big caveat found in testing:** the demo repo has been **renamed** from `Dustrunner369/study-spot-app` to `Dustrunner369/studease`. Normal REST calls follow the redirect, but search qualifiers do not. `repo:Dustrunner369/study-spot-app` silently returned 0 code results and a 422 on issue search. Store the canonical `full_name` from `GET /repos/{o}/{r}` when the repo is connected.

## The demo repo, measured

Measured on 2026-11-05 with the `gh` CLI. `GET /repos/Dustrunner369/study-spot-app` returns `full_name: "Dustrunner369/studease"`, public, default branch `main`.

- The recursive tree has 312 entries (233 files) and `truncated: false`. The tree response is about 13.5 KB as `size<TAB>path` lines.
- Most of the bytes are PNGs (48 files, 9.8 MB). Source code: 47 `.cs` files (471 KB) and 26 `.dart` files (321 KB).
- 27 files are under `backend/Migrations/`. Several `.Designer.cs` snapshots are 25 to 40 KB each, and they match almost any domain keyword.
- After dropping Migrations, `ios/` and `android/`, the text and source files (`.dart .cs .ts .html .sql .md .yaml`) come to 86 files and about 670 KB, roughly 170k tokens. That is too much to send whole in one call, but the path list alone is small.
- The repo has a `context/` folder of hand-written docs (README, api-contracts, data-model, mobile-app, schema.sql, and others), about 150 KB in total. It already summarises the system.
- Largest single files: `app-frontend/mobile/lib/main.dart` (63 KB) and `backend/Program.cs` (45 KB).
- Open issues: 0. Open PRs: 0. No issues have ever been opened.

## Options compared

### A. Code search (`GET /search/code`)

- **Fine-grained PAT:** supported. The endpoint page says *"The fine-grained token does not require any permissions."* It still only returns repos the token can see. Source: [Search code](https://docs.github.com/en/rest/search/search#search-code).
- **Branch and files:** *"Only the default branch is considered."* *"Only files smaller than 384 KB are searchable."* The query needs at least one search term (same source). The legacy search syntax page adds that the repo must have *"had activity or have been returned in search results in the last year"*, has fewer than 500,000 files, and is not archived. It also says *"At most, search results can show two fragments from the same file."* Source: [Searching code (legacy)](https://docs.github.com/en/search-github/searching-on-github/searching-code). The REST endpoint uses this legacy syntax, not the newer GitHub code search syntax.
- **Rate limit:** *"The Search code endpoint requires you to authenticate and limits you to 10 requests per minute"*, and other search endpoints allow 30/min. Source: [Search, rate limit](https://docs.github.com/en/rest/search/search#rate-limit).
- **Query limits:** at most 256 characters (not counting qualifiers) and at most 5 `AND`/`OR`/`NOT` operators; up to 1,000 results per search; 100 per page (same source).
- **Result shape:** `name`, `path`, `sha`, `html_url`, `score` and similar fields, but **no file contents**. With `Accept: application/vnd.github.text-match+json` each result carries `text_matches[].fragment`, a short snippet around the match. In testing these were about 150 characters. Getting a whole file takes another request to the contents API.
- **Tested:** `noise repo:Dustrunner369/studease` returned 30 files. 13 of them were EF migration files and several were privacy policy pages, so the noise is real. A natural-language phrase ("find a quiet place to study near me") returned 2 results. Plain keyword matching cannot grasp what a Problem is about, so the LLM (or code) would first have to turn the Problem into keywords.
- **Verdict:** cheap and permission-free, but noisy, keyword-only, returns fragments only, and has the tightest rate limit. Fine as an optional extra signal ("these files mention X"). Weak as the main way to pick files.

### B. Repo tree plus contents API, with the LLM picking files

- **Tree:** `GET /repos/{o}/{r}/git/trees/{sha}?recursive=1`. *"The limit for the tree array is 100,000 entries with a maximum size of 7 MB when using the recursive parameter."* The response sets `truncated: true` when the limit is passed. Each entry has `path`, `type`, `size` and `sha`. `{tree_sha}` can be a branch name such as `main` or `HEAD`. Needs **Contents: read**. Source: [Git trees](https://docs.github.com/en/rest/git/trees).
- **Files:** `GET /repos/{o}/{r}/contents/{path}?ref=…`. All features work up to 1 MB. For 1 to 100 MB only the `raw` or `object` media types work. *"Over 100 MB: not supported."* Use `Accept: application/vnd.github.raw+json` to get the plain file body with no base64 decoding. `ref` defaults to the default branch. Needs **Contents: read**. Source: [Repository contents](https://docs.github.com/en/rest/repos/contents).
- **Rate limit:** ordinary REST calls, 5,000 requests per hour per PAT; secondary limit of 900 points per minute, where a GET costs 1 point. Source: [REST rate limits](https://docs.github.com/en/rest/using-the-rest-api/rate-limits-for-the-rest-api). Ten or so requests per click is negligible.
- **How it works:** filter the tree in code by extension and folder (skip images, `ios/`, `android/`, `Migrations/`, lock files) → about 90 paths, roughly 3 to 4k tokens with sizes. A small "pick up to N files relevant to this Problem" LLM step returns paths → fetch them raw, cap each file (for example the first 30 KB) and the total (for example 120 KB) → main call.
- **Verdict:** it understands meaning (the model reads paths like `features/study_spots/presentation/add_spot_sheet.dart` and knows what they are), it is deterministic, it needs only Contents: read, and it has no index to keep current. The cost is a second, small LLM call, or a fixed-context variant (below) that keeps one call.

### B′. One-call variant: filtered tree plus `context/` docs as fixed context

Skip the picker step. Send the filtered path list plus the `context/*.md` files (about 150 KB, roughly 40k tokens, which could be trimmed to README, data-model, api-contracts and mobile-app at about 70 KB) plus open issues/PRs in the one Suggest Solutions call. This keeps "one call per click". The model sees what exists from the hand-written docs and the paths, but not the actual source. The risk is that docs drift from the code. The `context/` folder is specific to this repo, so for any other repo this falls back to the README plus the path list.

### C. Open issues and PRs: list vs search

- **List:** `GET /repos/{o}/{r}/issues?state=open&per_page=100`. *"GitHub's REST API considers every pull request an issue"*; PRs are the items that have a `pull_request` key. Needs **Issues: read**. Source: [Issues](https://docs.github.com/en/rest/issues/issues). This gives titles and bodies of both issues and PRs in one request. `GET /repos/{o}/{r}/pulls?state=open` (needs **Pull requests: read**) adds details such as head branch and draft status, plus `/pulls/{n}/files` for the changed paths. Source: [Pulls](https://docs.github.com/en/rest/pulls/pulls).
- **Search:** `GET /search/issues?q=repo:{o}/{r} is:open …`. Needs no fine-grained permission, allows 30 requests/min, and returns `text_matches` from title, body and comments. There is also a new `search_type=semantic|hybrid` option (embedding-based, 10/min, requires authentication), and an `advanced_search` flag. Source: [Search issues](https://docs.github.com/en/rest/search/search#search-issues-and-pull-requests). Note: *"For requests made by GitHub Apps with a user access token … requests that don't include the is:issue or is:pull-request qualifier will receive an HTTP 422."* That rule does not apply to PATs, but it would matter after a move to a GitHub App (assumption 7).
- **Verdict:** at demo volume (0 open today, and a handful after seeding), list everything and let the LLM judge relevance. Search only pays off with hundreds of open issues. Semantic issue search is worth remembering for that case.

### D. A pre-built index refreshed when the repo is connected

On connect (or with a "Refresh" button), walk the tree, fetch each source file, have an LLM write a one-line summary per file, and store it in the app's schema. Suggest Solutions then sends the summary list (86 lines) and picks files from it.

- **Pro:** the picker or single call sees what each file *does*, not just its name.
- **Con:** about 90 LLM calls per refresh, a new table, and a staleness problem (needs the tree `sha` or a push webhook to know when to refresh). This is more moving parts than a demo-grade build needs (assumption 2), and path names in this repo already describe the files well.
- **Verdict:** not worth it for the demo. It is the natural next step if repos get large or file names stop being descriptive.

### E. GraphQL

GraphQL can fetch several files' contents in one query (`repository { object(expression: "main:path") { ... on Blob { text } } }`), which would save requests. It brings no new search ability over REST, and the request count is not a constraint here, so it adds complexity for no real gain. Not researched further.

## Token permissions (fine-grained PAT, one repo)

| Need | Endpoint | Permission |
|---|---|---|
| Canonical name, default branch | `GET /repos/{o}/{r}` | Metadata: read (always granted) |
| File tree | `GET /repos/{o}/{r}/git/trees/{sha}` | Contents: read |
| File bodies | `GET /repos/{o}/{r}/contents/{path}` | Contents: read |
| Open issues (plus PRs, in brief) | `GET /repos/{o}/{r}/issues` | Issues: read |
| PR details or changed files (optional) | `GET /repos/{o}/{r}/pulls` | Pull requests: read |
| Code or issue search (optional) | `GET /search/code`, `/search/issues` | none |
| Filing issues (other features) | `POST /repos/{o}/{r}/issues` | Issues: read and write |

Sources: the per-endpoint "Fine-grained access tokens" boxes on each linked docs page, and [Permissions required for fine-grained PATs](https://docs.github.com/en/rest/authentication/permissions-required-for-fine-grained-personal-access-tokens). The ticket's planned token (Contents read, Issues read/write) should **add Pull requests: read** if PR details are wanted. Without it, open PRs still show up through the issues list.

## Recommendation

Use **B** (tree → LLM picks files → fetch raw → Suggest Solutions call) with **C-list** for issues and PRs. If "one AI call per click" is a hard rule, use **B′** (filtered tree plus `context/` docs plus the issues list, in one call) and accept that the model sees documentation rather than code. Code search (A) can be added later as a hint to the picker. It should not be the main mechanism.

## Risks and caveats

1. **Repo rename.** Search qualifiers do not follow redirects (tested: 0 code results and a 422 on issue search with the old name). Read `full_name` from `GET /repos/{o}/{r}` on connect and use it everywhere.
2. **Generated noise.** Without filtering, EF `Migrations/*.Designer.cs` and `ios/` and `android/` scaffolding take up context and confuse both search and the picker. Keep the deny-list in config.
3. **Large files.** `main.dart` (63 KB) and `Program.cs` (45 KB) are big. Cap or truncate each file, or the picker may spend the whole budget on 2 files.
4. **Default branch only.** Search, and the tree without an explicit ref, reflect `main`. Work in open PRs is visible only through the issues/PRs list, not the code. That fits "don't propose what's in progress", provided PR titles and bodies are included.
5. **Picker misses.** If the picker step leaves out a relevant file, the main call never sees it. Pass the full filtered path list to the main call as well, so the model can at least mention files by name.
6. **The `context/` docs are specific to this repo.** B′ depends on them, so a different connected repo would get a weaker result.
7. **Not verified with a real fine-grained PAT.** These tests used the `gh` CLI's own token against a public repo. The fine-grained permissions above come from GitHub's docs. Check them with the real token before the demo.
