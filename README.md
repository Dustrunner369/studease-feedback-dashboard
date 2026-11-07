# Studease Feature Request Intelligence System

## Results on the seed backlog

- **150 Feature Requests converted into 30 underlying customer problems -> 80% less for the product team to review**
- **92% duplicate catch rate** 
- **99% grouping accuracy**
- **92% priority consistency (How consistent the Agent is in its prioritization of feature requests)**
- **$0.042 in AI costs to process all 150 requests (under 3¢ per 100 requests)**

From one seed run and one consistency check on 6 Nov 2026. Full numbers are in [`metrics.txt`](metrics.txt).

The Studease Feature Intelligence System: a public portal where students submit and upvote Feature Requests, and an `/admin` workspace where the product team triages the Problems the AI groups them into. See the spec, [Spec: Studease Feature Intelligence System (v1)](https://github.com/Dustrunner369/studease-feedback-dashboard/issues/17), and the terms in [`GLOSSARY.md`](GLOSSARY.md).

## What you need

- .NET 9 SDK (`global.json` pins 9.0.301)
- Docker Desktop, running (for the local database and the tests)
- An OpenAI API key, and a GitHub fine-grained token for Suggest Solutions and GitHub issues

Then get the code:

```bash
git clone https://github.com/Dustrunner369/studease-feedback-dashboard.git
cd studease-feedback-dashboard
```

To get from a fresh clone to a seeded app, follow [Locally: .NET user secrets](#locally-net-user-secrets) and then the steps in [Run it locally](#run-it-locally).

## Secrets

The app reads three secrets. None of them go in a file in the repo.

| Key | What it's for |
|---|---|
| `OpenAI:ApiKey` | Every AI step: matching, scoring, Decision Briefs, Requester update drafts, Suggest Solutions. Without it, every Feature Request stays unmatched. |
| `Admin:Key` | The key you type to sign in to `/admin`. Pick any long random string. With no key set, nobody can sign in. |
| `GitHub:Token` | A fine-grained token for `Dustrunner369/studease` only: Contents read, Issues read/write, Metadata read. |

### Locally: .NET user secrets

User secrets are stored in your user profile, outside the repo. Run these in your own terminal:

```bash
dotnet user-secrets init --project src/Feedback.Api      # harmless if the .csproj already has a UserSecretsId

dotnet user-secrets set "OpenAI:ApiKey" "sk-..."                 --project src/Feedback.Api
dotnet user-secrets set "Admin:Key"     "<a long random string>" --project src/Feedback.Api
dotnet user-secrets set "GitHub:Token"  "github_pat_..."         --project src/Feedback.Api

dotnet user-secrets list --project src/Feedback.Api      # check them (prints the values)
```

User secrets only load in the Development environment. That's the default for `dotnet run --project src/Feedback.Api`, the seed commands and `dotnet ef`.

### In production: Fly secrets

Double underscores stand in for the colons:

```bash
fly secrets set OpenAI__ApiKey="sk-..." Admin__Key="..." GitHub__Token="github_pat_..." \
  ConnectionStrings__Feedback="Host=...;Database=...;Username=...;Password=...;Search Path=feedback"
```

The production connection string must include `Search Path=feedback`. The app keeps all its tables in the `feedback` schema and never touches the existing Studease tables.

## Run it locally

1. **Start a local Postgres with pgvector** on port 5433. The connection string for it is already in `appsettings.Development.json`:

   ```bash
   docker run -d --name feedback-dev-db -e POSTGRES_PASSWORD=postgres -e POSTGRES_DB=feedback \
     -p 5433:5432 pgvector/pgvector:0.8.6-pg18
   docker exec feedback-dev-db psql -U postgres -d feedback \
     -c "CREATE SCHEMA IF NOT EXISTS feedback; CREATE EXTENSION IF NOT EXISTS vector SCHEMA feedback;"
   ```

   The vector extension lives in the `feedback` schema, as it does in production. The migrations never create it.

2. **Create the tables:**

   ```bash
   dotnet tool restore                          # once; installs dotnet-ef
   dotnet ef database update -p src/Feedback.Api
   ```

   The app doesn't migrate on startup, so run this again after pulling new migrations.

3. **Run the app:**

   ```bash
   dotnet run --project src/Feedback.Api
   ```

   The portal is at http://localhost:5242 and the product team's workspace is at http://localhost:5242/admin (sign in with your `Admin:Key`).

4. **Load the demo data (optional).** This sends 150 synthetic Feature Requests from `seed/feature-requests.json` through the real AI pipeline, so it needs `OpenAI:ApiKey` set. It takes about 10 minutes and cost $0.042 on the last run. Run it in a second terminal (the app from step 3 can keep running):

   ```bash
   dotnet run --project src/Feedback.Api -- seed load      # refuses if the schema already has data
   dotnet run --project src/Feedback.Api -- seed remove    # empties every feedback table; keeps the schema
   ```

   `seed load` ends by printing the duplicate catch rate and grouping accuracy, and saves them to `/admin` → Metrics. To start over, run `seed remove` and then `seed load` again. Both commands use whichever `ConnectionStrings:Feedback` is configured, so check which database you're pointed at first.

5. **Check priority consistency (optional).** In `/admin` → Metrics, run the Priority consistency check. It re-scores every open Problem 3 times (about 78 AI calls on the seed data, roughly 1.5¢) and shows how many kept the same level.

## Tests

```bash
dotnet test
```

The tests need Docker running: they start their own throwaway Postgres + pgvector container. The AI and GitHub are replaced by fakes, so the tests need no keys and make no real API calls.

## Other config

Non-secret settings are in `src/Feedback.Api/appsettings.json`:

- `Ai:Steps`: the model and reasoning effort for each AI step
- `PriorityCriteria`: the criteria the Recommended Priority is scored against (read-only in `/admin`)
- `Suggest`: the suggest-as-you-type thresholds (placeholders until tuned on real embeddings)
- `GitHub:Repo`: the connected repo
- `Portal:RateLimit`: the per-IP rate limit (`ClientIpHeader` is `Fly-Client-IP` for production)
