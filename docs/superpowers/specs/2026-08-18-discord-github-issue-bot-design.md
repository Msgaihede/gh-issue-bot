# Discord → GitHub Issue Bot — Design

**Date:** 2026-08-18
**Status:** Approved

## Overview

A Discord bot that lets app users report bugs and request features from Discord.
AI normalizes the report, deduplicates it against existing GitHub issues
(embeddings + LLM verdict), and — after user confirmation — creates a
well-written GitHub issue or comments on an existing one. Screenshots attached
in Discord are uploaded to GitHub and embedded in the issue.

## Goals

- `/report-issue` and `/request-feature` slash commands opening a modal
  (description + optional screenshots, app selector when a guild maps to
  multiple apps).
- AI dedup: normalize → embed → cosine top-5 → LLM structured verdict.
- Preview + confirm before anything lands on GitHub (ephemeral).
- Match handling: comment on open issues; for issues closed <30 days, ask
  "still happening?" and create a new issue referencing the old one if yes.
- `/issues`: ephemeral list of open issue titles + links.
- Created issues announced publicly in configured channel(s).
- Multi-app: several GitHub repos, each mapped to Discord guilds + channels.
- Docker-ready; config via JSON + env vars + Docker secrets in any combination.

## Non-goals (v1)

- GitHub webhooks / reverse sync (Discord is not notified when issues change).
- Issue editing, closing, or triage from Discord.
- Vector database / ANN index — in-memory cosine over hundreds of issues is fine.
- GitHub App auth (PATs only).

## Architecture

Single worker project + test project (Approach A). Console app on the .NET
Generic Host; Discord.Net gateway connection in a `BackgroundService` (no
public ingress needed — rejected interactions-webhook approach for that
reason). Strict folder/namespace boundaries; every external boundary behind an
interface so tests can mock.

```
src/DiscordGithubBot/
  Program.cs                 host wiring, DI, config, EF migration on boot
  Configuration/             options records + startup validation
  Data/                      BotDbContext, entities, value converters
  Discord/                   BotService, interaction modules, component builders
  GitHub/                    GitHub REST client, two-tier image uploader
  Ai/                        report normalizer, duplicate detector
  Pipeline/                  ReportPipeline orchestrating a report end-to-end
tests/DiscordGithubBot.Tests/
APP.md  CLAUDE.md  docs/DECISIONS.md
Dockerfile  .dockerignore  docker-compose.yml  .gitignore  .env.example
```

### Packages (pinned)

| Package | Version | Purpose |
|---|---|---|
| Discord.Net | 3.20.1 | gateway + interaction framework (modal file upload needs ≥3.19, attribute binding ≥3.20) |
| Microsoft.Extensions.AI | 10.9.0 | `IChatClient`, `IEmbeddingGenerator` abstractions |
| Microsoft.Extensions.AI.OpenAI | 10.9.0 | OpenAI adapter (GA) |
| OpenAI | 2.13.0 | underlying SDK |
| Microsoft.EntityFrameworkCore.Sqlite | 10.0.11 | storage |
| Microsoft.Extensions.Configuration.KeyPerFile | 10.0.11 | Docker secrets |
| Microsoft.Extensions.Hosting | 10.0.x | Generic Host |
| System.Numerics.Tensors | 10.0.x | `TensorPrimitives.CosineSimilarity` |
| xunit + Microsoft.NET.Test.Sdk | current | tests |

Notes: do not use EF Core 11 previews (target .NET 11). Octokit.NET rejected
(stale, can't call the unofficial upload endpoint) — plain `HttpClient`.
Discord.Net 3.20 renamed `SelectMenuOptionAttribute` → `EnumOptionAttribute`.

## Report pipeline (core flow)

1. User invokes `/report-issue` (bug) or `/request-feature` (enhancement).
   Modal opens: App string-select (only when the guild maps to >1 app),
   multiline Description, optional File Upload (0–10 images).
2. On modal submit: **download attachment bytes immediately** (Discord CDN
   URLs expire ~24 h), then `DeferAsync(ephemeral: true)` — ephemerality is
   locked at defer time; all slow work happens after the defer.
3. Normalize with `gpt-5.6-luna` → structured `{title, body}` draft using a
   bug or feature template; body credits the reporting Discord user.
4. Embed normalized text with `text-embedding-3-small` (1536 dims — dimension
   fixed in exactly one constant).
5. Incremental issue sync for the target repo (see Data model), then
   in-memory cosine top-5 over candidates (open OR closed ≤30 days).
6. `gpt-5.6-luna` structured verdict: `duplicate_of(n) | uncertain(candidates) | no_match`.
   Structured-output parse failure degrades to `uncertain`.
7. Outcome handling (all ephemeral, Components V2):
   - **Match, open issue** → show it: `[Same issue → add my report as comment]`
     `[Not it → show my draft]`
   - **Match, closed <30 d** → "Looks like #X, closed N days ago — still
     happening?" `[Yes → draft new issue referencing #X]` `[No → link to the
     closed issue]` (ends flow)
   - **Uncertain** → candidate select + `[None of these → my draft]`;
     selecting a candidate routes into the matching open/closed flow above
   - **No match** → draft preview `[Create issue]` `[Cancel]`
8. **Every** issue-creating path goes through the draft preview + confirm.
9. On create: upload images, create issue with label (`bug`/`enhancement`),
   post a public Components V2 announcement in the app's configured
   channel(s), confirm ephemerally. On comment: upload images, add comment,
   confirm ephemerally (no channel post — only creations are announced).

### Pending state

Between modal submit and button click, state persists in SQLite
(`PendingReport` + `PendingAttachment` blobs). Button custom-ids carry the
pending report GUID. TTL 1 hour, background cleanup. Survives bot restarts;
expired-state clicks get a polite "session expired, please re-submit" reply.

## Configuration

Layering (last wins): `appsettings.json` → `appsettings.{Environment}.json` →
environment variables (`__` delimiter) → `AddKeyPerFile("/run/secrets",
optional: true)` so Docker secrets always win.

Apps are a **list** with a unique `Repo` key — not a dictionary keyed by repo,
because `/` in `owner/repo` cannot appear in environment-variable names, which
would break env-var overrides. Uniqueness of `Repo` is validated at startup.

```json
{
  "Discord": { "Token": "<secret>" },
  "OpenAI": {
    "ApiKey": "<secret>",
    "ChatModel": "gpt-5.6-luna",
    "EmbeddingModel": "text-embedding-3-small"
  },
  "Database": { "Path": "./db/app.db" },
  "Apps": [
    {
      "Name": "MyApp",
      "Repo": "owner/repo",
      "GitHubToken": "<secret>",
      "GuildIds": [111111111111111111],
      "ChannelIds": [222222222222222222]
    }
  ]
}
```

Startup validation (fail fast): ≥1 app; per app: unique non-empty
`owner/repo`, non-empty token, ≥1 guild id, ≥1 channel id; Discord token and
OpenAI key present. Model ids default to `gpt-5.6-luna` (the bare `gpt-5.6`
alias routes to the ~10× more expensive Sol tier — never default to it) and
`text-embedding-3-small`.

Secrets hygiene: `.gitignore` covers `.env`, `secrets/`, `db/`,
`appsettings.*.local.json`; `.env.example` documents every knob.

## Data model (EF Core, SQLite at `./db/app.db`)

- **IssueEmbedding** — `RepoKey`, `IssueNumber`, `Title`, `State`,
  `ClosedAtUtc?`, `UpdatedAtUtc`, `ContentHash`, `Vector` (`float[]` ↔ BLOB
  value converter via `MemoryMarshal`, plus a `ValueComparer` — mandatory for
  mutable arrays). Unique index (`RepoKey`,`IssueNumber`).
- **PendingReport** — `Id` (GUID), `RepoKey`, `DiscordUserId`,
  `ReporterDisplayName`, `Type` (Bug|Feature), `OriginalText`, `DraftTitle`,
  `DraftBody`, `CandidatesJson`, `CreatedAtUtc`.
- **PendingAttachment** — FK to PendingReport, `FileName`, `ContentType`,
  `Bytes` (BLOB).
- **RepoSyncState** — `RepoKey`, `LastSyncUtc`.

Issue sync: `GET /repos/{o}/{r}/issues?state=all&since={LastSyncUtc}&per_page=100`
(paginate; filter out PRs — items with a `pull_request` key). Re-embed only
when `ContentHash(title + body)` changes. Candidate query filters
`State == open || ClosedAtUtc >= now − 30 d`; stale rows pruned
opportunistically.

## GitHub integration

Plain `HttpClient` typed client, one instance per configured app (token in
`Authorization: Bearer`). Operations: create issue (with label), comment,
list open issues, list issues since (sync), upload image.

**Images — two-tier strategy:**
1. Primary: unofficial `POST https://uploads.github.com/user-attachments/assets`
   (same endpoint the web UI drag-drop uses). Permanent URLs; renders inline
   in public **and private** repos. Undocumented; PAT compatibility unverified
   → a day-one smoke test runs against the real PAT; 401/404 at runtime
   triggers the fallback.
2. Fallback: official Contents API `PUT
   /repos/{o}/{r}/contents/issue-assets/{yyyyMMddHHmmss}-{n}.{ext}` on an
   orphan `issue-assets` branch; embed `raw.githubusercontent.com` URLs
   (inline rendering only on public repos; bare links on private).

Image upload failure never blocks issue creation — the issue body notes
"screenshot upload failed". Discord CDN URLs are **never** hotlinked (they
expire). PAT requirements: fine-grained → Issues:write + Contents:write;
classic → `repo`.

## AI integration

`Microsoft.Extensions.AI` abstractions registered in DI:

- `IChatClient` ← `openAIClient.GetChatClient(chatModel).AsIChatClient()`
- `IEmbeddingGenerator<string, Embedding<float>>` ←
  `openAIClient.GetEmbeddingClient(embeddingModel).AsIEmbeddingGenerator()`

Structured outputs via `GetResponseAsync<T>(...)` with `TryGetResult` — parse
failures degrade gracefully (normalizer: retry once then error out
ephemerally; verdict: degrade to `uncertain`).

Two prompt surfaces, kept in code as constants with the templates:
1. **Normalizer** — turns raw user text into `{title, body}`; bug template
   (steps/expected/actual if inferable) vs feature template; never invents
   facts not in the report; output language: English.
2. **Duplicate judge** — given normalized report + top-5 candidates
   (number/title/state/body-excerpt), returns
   `{verdict: match|uncertain|no_match, issueNumber?, candidates?[]}`.

## Discord layer

- Discord.Net Interaction Framework, `InteractionServiceConfig.UseCompiledLambda = true`
  (documented `IModal` perf issue otherwise).
- Typed `IModal` classes; `[ModalFileUpload]` binds to `IAttachment[]`.
- Slash commands registered per configured guild
  (`RegisterCommandsToGuildAsync`) on Ready — no global registration.
- `BotService : BackgroundService` — login, wire `InteractionCreated`,
  register commands, reconnect handling.
- Components V2 for all rich replies (container/section/text-display,
  media gallery for screenshots in the channel announcement). CV2 rule: no
  `content`/`embeds` on CV2 messages; after a defer the CV2 flag goes on the
  follow-up, not the defer.
- `/issues [app]` — app parameter auto-resolved when the guild maps to exactly
  one app, required select otherwise; ephemeral paginated list of open issue
  titles linking to GitHub.

## Error handling

- Defer before slow work; every failure path ends in an ephemeral message —
  never a hung interaction.
- OpenAI/GitHub outage → ephemeral apology + structured log (ILogger).
- Attachment download failure → proceed without that file, note it to the user.
- Unknown/expired pending-report id → "session expired" reply.
- Config errors → fail startup with a message naming the offending key.

## Testing (XUnit)

Unit tests per feature, written when the feature lands:
- Cosine ranking + top-k selection.
- Verdict routing — all four outcome paths.
- Config binding + validation (valid, missing token, duplicate repo, env-var
  override, key-per-file override).
- `float[]` ↔ BLOB converter round-trip; EF model with SQLite in-memory.
- Issue body formatting (bug/feature templates, reporter credit, image links,
  regression reference).
- Two-tier image uploader fallback (fake `HttpMessageHandler`).
- Issue sync: pagination, PR filtering, hash-based re-embed skip, 30-day window.
- Mock `IChatClient` / `IEmbeddingGenerator` for pipeline tests.

Discord modules stay thin (parse interaction → call pipeline → render result)
and are not unit-tested; all logic lives in testable services.

## Docker

- Multi-stage: `mcr.microsoft.com/dotnet/sdk:10.0` build →
  `mcr.microsoft.com/dotnet/runtime:10.0` (Ubuntu Noble; .NET 10 has no
  Debian images).
- Non-root: create + chown the db directory **before** `USER $APP_UID`
  (otherwise SQLite "unable to open database file" on the volume).
- `docker-compose.yml`: bot service, named volume for `./db`, secrets for
  Discord token / OpenAI key / GitHub PATs, `env_file: .env` support.
- `.dockerignore` excludes `db/`, `secrets/`, `.env`, build output.

## Documentation & working rules

- `APP.md` — what the app does, workflow, config reference, commands.
- `CLAUDE.md` — working rules: commit per feature with good messages; write
  unit tests after finishing a feature; run build + tests before committing;
  use the question tool when unclear; keep APP.md/CLAUDE.md/docs current.
- `docs/DECISIONS.md` — running decision log; today's decisions recorded there.

## Decisions log (as of this design)

1. Preview + confirm before creating any GitHub issue (no auto-create).
2. Closed-issue match: ask "still happening?" → yes: new issue referencing the
   old; no: link the closed issue, end.
3. Images: two-tier (unofficial user-attachments endpoint → Contents API
   orphan-branch fallback); never hotlink Discord CDN.
4. Apps configured as a list with unique `Repo` (env-var-safe), not a dict.
5. Single-project architecture (Approach A); gateway, not webhook.
6. Plain `HttpClient` for GitHub; no Octokit.
7. Pin `gpt-5.6-luna` (bare `gpt-5.6` alias = expensive Sol tier);
   `text-embedding-3-small` @ 1536 dims.
8. Embeddings in SQLite BLOBs; in-memory cosine via `TensorPrimitives`; no
   vector DB.
9. Pending report state persisted in SQLite with 1 h TTL.
10. Only issue creations are announced in channels; comments confirm
    ephemerally.
