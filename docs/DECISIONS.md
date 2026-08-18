# Decisions Log

Running log of non-obvious decisions made on this project. Each entry: date +
one paragraph. Seeded from the design spec's "Decisions log" section
(`docs/superpowers/specs/2026-08-18-discord-github-issue-bot-design.md`).

## 2026-08-18

1. **Preview + confirm before creating any GitHub issue.** No auto-create —
   every issue-creating path goes through a draft preview with an explicit
   confirm step, so a bad AI normalization or a bad dedup match never lands
   on GitHub unseen.

2. **Closed-issue match asks "still happening?"** When the dedup match is a
   closed issue closed less than 30 days ago, the bot asks the reporter if
   it's still happening: yes creates a new issue that references the old one;
   no links the closed issue and ends the flow.

3. **Two-tier image upload; never hotlink Discord CDN.** Screenshots try the
   unofficial `user-attachments` upload endpoint first (permanent URLs,
   renders inline on public and private repos), falling back to the official
   Contents API on an orphan `issue-assets` branch if that fails. Discord CDN
   URLs expire after ~24h and are never embedded directly in issue bodies.

4. **Apps are a list with a unique `Repo` key, not a dictionary.** A
   dictionary keyed by `owner/repo` would break environment-variable
   overrides, since `/` cannot appear in an env-var name. `Repo` uniqueness
   is validated at startup instead.

5. **Single-project architecture (Approach A); gateway, not webhook.** One
   worker project plus one test project. Discord.Net connects via the
   gateway inside a `BackgroundService` — no public ingress is needed, which
   ruled out the interactions-webhook approach.

6. **Plain `HttpClient` for GitHub; no Octokit.** Octokit.NET is stale and
   can't call the unofficial image-upload endpoint, so the GitHub integration
   is a hand-rolled typed `HttpClient` client instead.

7. **Pin `gpt-5.6-luna`; embeddings at 1536 dims.** The bare `gpt-5.6` alias
   routes to the ~10x more expensive Sol tier and must never be the default.
   Embeddings use `text-embedding-3-small` at 1536 dimensions, with the
   dimension fixed in exactly one constant.

8. **Embeddings in SQLite BLOBs; in-memory cosine; no vector DB.** Issue
   embeddings are stored as `float[]` mapped to BLOB via a value converter,
   and duplicate candidates are ranked with in-memory cosine similarity via
   `TensorPrimitives`. A dedicated vector database is unnecessary at the
   expected scale (hundreds of issues per repo).

9. **Pending report state persisted in SQLite with a 1 hour TTL.** Between a
   modal submit and the follow-up button click, report state (and any
   attachment bytes) lives in SQLite rather than in memory, so it survives
   bot restarts; a background job prunes expired entries.

10. **Only issue creations are announced in channels.** Creating a new GitHub
    issue posts a public announcement in the app's configured channel(s);
    adding a comment to an existing issue only confirms ephemerally to the
    reporter and does not post publicly.

## 2026-08-18 (scaffold follow-up)

11. Pinned `OpenAI` down from 2.13.0 to 2.12.0 to stay within
    `Microsoft.Extensions.AI.OpenAI` 10.9.0's declared dependency range
    (`>= 2.12.0 && < 2.13.0`); NU1608 hygiene — builds must be warning-free.

## 2026-08-18 (data model)

12. **Schema created with `EnsureCreated()`, no EF migrations.** The bot owns
    its SQLite file end to end and ships no migration history; the schema is
    materialized with `EnsureCreated()` at startup (and in tests against an
    in-memory connection). `IssueEmbedding.Vector` maps to a BLOB through a
    `ValueConverter<float[], byte[]>` over `VectorConversion`, paired with a
    sequence-equality `ValueComparer<float[]>` — without the comparer, EF
    change tracking would treat the mutable array by reference and miss
    in-place edits.
