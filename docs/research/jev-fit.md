# Is TypeSafe AI's Jev a good fit for this project's AI jobs?

Researched 2026-11-05. All facts below come from TypeSafe's own site and docs, or from OpenAI's own docs, unless marked otherwise.

## Short answer

Jev is real and you can use it, but only through early access. It **makes decisions only**: it returns typed answers (choice, score, a 0-1 truth value, a bounding box) with probabilities and confidence. It **does not generate text** and the docs **never mention embeddings**. So:

| Job | Does Jev fit? | Recommended model |
|---|---|---|
| (a) Similar-request embeddings (pgvector) | No. Jev produces no vectors | OpenAI `text-embedding-3-small` |
| (b) Map a request to a Problem, set category and priority | Partly. It fits the decisions, but it returns no written rationale | Embeddings shortlist, then Jev (Choice/Noul/Score), or an LLM with structured output if a written rationale is required |
| (c) Cited decision brief | No. It writes no text | A text-generation LLM |
| (d) Suggest Solutions from repo and issues | No. It writes no text | A text-generation LLM |
| (e) Draft email updates | No. It writes no text | A text-generation LLM |

## 1. Does it exist, and how do you get access?

- TypeSafe AI calls itself an AI lab building "machine-native intelligence infrastructure for automation". Jev is its "first public System One Model", offered "in early access". Source: https://typesafe.ai
- The announcement post is dated 2026-09-15. It says early access is still being rolled out from a waitlist: "bringing developers off the waitlist as quickly as we can". The model is not generally available. Source: https://typesafe.ai/blog/introducing-system-one-models-and-jev
- API keys come from the console at https://console.typesafe.ai/keys. Source: https://docs.typesafe.ai/introduction/quickstart.md
- Current models are `jev-1.14.0` (available from 2026-10-14) and `jev-1.13.0`. The alias `jev-latest` points to 1.14.0. `jev-preview` currently points to the same build, because no preview build exists. Aliases move when a new release ships, so pin a versioned ID if you need stable behaviour. No deprecation policy is published. Source: https://docs.typesafe.ai/models.md
- Pricing is **$0.042 per 1M input tokens** ($42 per billion), and output tokens are free. The blog says TypeSafe "cannot rule out" that this price is subsidised. Sources: https://docs.typesafe.ai/models.md, the blog post above.
- Third parties also mention access through OpenRouter, Cloudflare Workers AI, MotherDuck and LiteLLM. These are secondary sources and were not verified here.

## 2. What it does

- **Input:** one `state` (text, JSON or images) plus a set of named `questions`. All questions are answered in parallel against the same state. Source: https://docs.typesafe.ai
- **Question types:**
  - `choice`: picks from up to 255 options and returns `choice`, `probabilities` and `confidence`.
  - `score`: places the state on an ordinal scale defined by criteria and returns `score`, `probabilities` and `confidence`.
  - `noul`: a 0-1 truth judgment on a statement.
  - `box`: locates something in an image (Jev 1.14 only).

  Sources: https://docs.typesafe.ai/primitives/choice.md, https://docs.typesafe.ai/models.md
- **No free text:** Jev "gives up string generation" (announcement blog).
- **No embeddings:** none of the docs list an embedding output. The re-ranking cookbook first gets a shortlist from a separate retriever (BM25, or "dense embeddings" from some other model), then has Jev score each candidate pair. Source: https://docs.typesafe.ai/cookbooks/rerank_typesafe.md
- **No rationale:** a Choice answer contains only `type`, `choice`, `probabilities` and `confidence`.
- **"None of the above":** the docs say a none-of-the-above option "may not work well in many use cases". They recommend a separate companion Noul question that asks whether any option fits. Source: https://docs.typesafe.ai/primitives/choice.md
- **Context:** 64k tokens per request and 32k per question. A `TypeSafe-Danger-Zone-Long-Context: true` header raises this to 262k, but it is slower and less accurate. Source: https://docs.typesafe.ai/models.md
- **Latency:** TypeSafe claims 70-500 ms end to end. These are its own figures, measured from West Coast laptops. Its comparison workflow took 0.114 s and cost $0.000081, against 8.566 s and $0.013880 for an LLM. These are vendor benchmarks and have not been independently checked. Sources: the blog post and https://typesafe.ai

## 3. How you call it

- Endpoint: `POST https://api.typesafe.ai/v1/systemone`
- Headers: `Authorization: Bearer <key>` and `Content-Type: application/json`
- The body has the form `{ "state", "model", "questions": { name: { type, instructions, criteria } } }`.
- The response has the form `{ "model", "answers": {...}, "usage": { input_tokens, output_tokens } }`.

  Source for the four items above: https://docs.typesafe.ai/introduction/quickstart.md
- There is also a list-models endpoint. An OpenAPI spec is published at https://api.typesafe.ai/openapi.json (linked from https://docs.typesafe.ai/llms.txt).
- **SDKs:** official SDKs exist only for Python (`typesafe-sdk`) and JavaScript/TypeScript (`@typesafe-ai/sdk`). There is **no .NET/C# SDK**; the docs say "call the HTTP API directly from any language". From .NET 9 you would use `HttpClient` with your own DTOs, or generate a client from the OpenAPI spec. Source: https://docs.typesafe.ai/sdk.md
- **Limits:** the request body may be at most 32 MiB, with at most 64 nesting levels. Errors are 400 (`max_tokens_exceeded` when context is exceeded), 401, 413, 415, 422, 429 (rate limit; use exponential backoff) and 529 (overloaded). Rate limits are set per organisation, measured in tokens per second and requests per second, and shown in the console. No numbers are published. Sources: https://docs.typesafe.ai/api/errors.md, https://docs.typesafe.ai/models.md

## 4. How well it fits each job

- **(a) Embeddings for similar Feature Requests: no fit.** Jev produces no vectors, so it cannot fill a pgvector column. At most it could re-rank a shortlist that pgvector already found, as in the re-ranking cookbook. That adds a network round trip on every keystroke-debounced query, so it is optional and not needed for the first version.
- **(b) Map to a Problem, assign category and priority: good fit for the decisions, weak for observability.**
  - Category is a natural `choice` and suggested priority a natural `score`, each with the written business criteria passed as `criteria`.
  - Problem matching works with this pattern:
    1. Use pgvector to shortlist the top-N existing Problems. This keeps the list under the 255-option cap and inside the context limit.
    2. Ask a `choice` over that shortlist plus a companion `noul` ("this request matches one of these Problems"). A low noul or low confidence means "create a new Problem".
  - The catch is that Jev returns **no written rationale**. You can store the probabilities, confidence, model version, the inputs and the criteria, and that is reasonably good observability. But if the brief or the UI needs a human-readable "why", you need an LLM call for it, either for every request or only when confidence is low.
  - If one text LLM already handles (c) to (e), using the same LLM with structured output for (b) is simpler: one vendor, one .NET SDK, and the rationale comes free. Jev is the cheaper and faster option to adopt later if volume or latency justifies it.
- **(c) Cited decision brief: no fit.** It needs free text. Jev could optionally check citations afterwards (see the "Double-checking citations" cookbook in llms.txt), but it cannot write the brief.
- **(d) Suggest Solutions from repo code and issues: no fit.** It needs free text.
- **(e) Draft email updates: no fit.** It needs free text.

## 5. OpenAI embeddings (the fallback)

| Model | Default dimensions | `dimensions` parameter | Max input | Price per 1M tokens |
|---|---|---|---|---|
| `text-embedding-3-small` | 1536 | supported | 8192 tokens | $0.02 |
| `text-embedding-3-large` | 3072 | supported | 8192 tokens | $0.13 |
| `text-embedding-ada-002` (older) | 1536 (not stated on the guide page) | no | 8192 tokens | $0.10 |

Sources: https://developers.openai.com/api/docs/guides/embeddings, https://developers.openai.com/api/docs/pricing. The pricing page lists no separate batch price for embeddings.

`text-embedding-3-small` at 1536 dimensions fits pgvector's indexable limit (HNSW/IVFFlat support up to 2000 dimensions for `vector`, from pgvector's README, not re-verified here). That makes it the clear choice for job (a). `3-large` at 3072 dimensions would need either `dimensions` set to 2000 or less, or the `halfvec` type.

Jobs (c), (d) and (e), and possibly (b) for the rationale, need a text-generation LLM whichever embedding provider is chosen.

## Open caveats

- Jev is in waitlisted early access. Getting a key is not guaranteed, and pricing may be subsidised and could change.
- No published rate-limit numbers. No .NET SDK. No deprecation policy.
- The latency and cost advantages are TypeSafe's own benchmarks.
- Whether the assignment requires a *written* rationale for (b) decides between Jev and an LLM for that job. Check it against the assignment brief.
