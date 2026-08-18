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

## 2026-08-18 (image uploader)

13. **`issue-assets` branches from the default branch's HEAD, not orphaned.**
    The design spec called for an orphan `issue-assets` branch, but the REST
    API cannot create one: `POST /git/refs` requires a starting SHA. Creating
    a true orphan would mean hand-building an empty tree and a parentless
    commit through the git-data API — three extra calls for a fallback path
    that only stores screenshots. The branch is therefore created from the
    default branch's HEAD (`GET /git/ref/heads/{default}` → `POST /git/refs`),
    which costs one duplicated history and nothing else.

14. **Lenient parsing of the unofficial upload response.** The
    `uploads.github.com/user-attachments/assets` response is undocumented and
    unversioned, so the URL is located rather than deserialized: `href`,
    `url`, `asset_url` are checked first, then any root-level string property
    containing `user-attachments/assets`. Any tier-1 failure — non-2xx,
    exception, or a body with no recognizable URL — is logged at warning level
    and falls through to the Contents API; only both tiers failing is an error,
    and then `UploadAsync` returns `null` so the caller notes the failure and
    keeps going. Raw URLs are
    `raw.githubusercontent.com/{owner}/{repo}/issue-assets/issue-assets/{file}`:
    the repeated segment is the branch ref followed by the folder, both named
    `issue-assets`. Upload paths get a `yyyyMMddHHmmssfff` prefix so every
    upload is a new file (no existing-blob SHA needed), and file names are
    reduced to ASCII letters, digits, `.`, `-`, `_` so the URL never needs
    escaping.

## 2026-08-18 (AI services)

15. **Normalization retries thrown calls too, but never cancellation.** The
    task contract specified a retry when the structured-output layer returns
    no result or a blank title; the retry also covers an exception from the
    chat client, because a transient 429/500 is the likeliest real first-attempt
    failure and the caller's contract is simply "throws
    `NormalizationException` after one retry". `OperationCanceledException` is
    rethrown before that catch, so a cancelled request is never retried nor
    disguised as a normalization failure. Both services read model output with
    `ChatResponse<T>.TryGetResult`, never `.Result`: malformed output is an
    expected case with a defined fallback, not an exception.

16. **Every duplicate-judge failure degrades to Uncertain over all
    candidates, in ranked order.** Unparseable output, a thrown call, an
    unknown verdict string, and a `match` naming an issue number that was
    never offered all map to `Uncertain` over the full input list — asking the
    reporter beats guessing, and the hallucinated-number check keeps a
    confident-sounding wrong answer from creating a comment on an unrelated
    issue. For an `uncertain` verdict the shortlist is computed as
    `inputNumbers.Where(modelNumbers.Contains)`, which preserves the
    vector-ranked input order rather than the model's emission order, so the
    reporter sees the most similar issue first; an empty intersection falls
    back to all candidates. `CandidateNumbers` is left empty for `Match` and
    `NoMatch`, matching the contract's documentation of the field as the
    numbers worth showing when the verdict is `Uncertain` — for a match the
    number is already in `IssueNumber`.

17. **Normalizer prompt preserves facts and translates to English.** Beyond
    the required per-type section lists and the 80-character imperative title
    rule, the prompt tells the model to preserve the reporter's facts exactly
    and correct only grammar, spelling, and structure, and to translate a
    non-English report into English. Discord reporters write in whatever
    language they please while the repositories' issues are English; pairing
    the translation instruction with an explicit "never invent details …
    omit that whole section rather than guessing" keeps rewriting from
    drifting into authoring. Prompt inputs are bounded — raw report to 4000
    characters, each candidate body excerpt to 1000 — so a pasted log cannot
    blow the context budget.
