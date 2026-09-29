# OpenRouter + Jev Redesign — Design

**Date:** 2026-09-29
**Status:** Approved (requirements from the owner; open points settled by Q&A)
**Supersedes:** the AI, dedup and command sections of
`2026-08-18-discord-github-issue-bot-design.md`. Everything not mentioned here
(attachments, image upload, pending-report claims, GitHub auth, Docker, CI)
is unchanged.

## Requirements

1. One command creates issues: `/issue`. Separate bug/feature commands go;
   **Jev decides the type**.
2. Dedup against the repo's other **open** issues by *reading their
   contents* — the embedding/KNN approach is removed.
3. Issues are tagged with **the repo's own labels**, every one that fits.
4. The bot is **user-installable**; `/issue-install` hands out the install
   link.
5. **All AI traffic goes through OpenRouter.** GPT-6 Luna for generation,
   flex first with fallback to the regular tier; Jev (System One) for
   classification and decisions.
6. Issues are **enriched with code context**: the relevant files are listed
   and briefly explained, found through a cached **repository map** so a
   report never trawls the code base.
7. **500–1000 issues/month for $5–10/month.**
8. The issue **title must represent the actual issue.**
9. Relevant **Markdown docs** are scanned too — repositories keep useful
   context in `.md` files (added during implementation).

Settled with the owner: `/issue` + `/issue-install` (Discord forbids a
runnable `/issue` with subcommands); `/issues` stays, later renamed
`/list-issues`. From a user install, the apps of the configured servers the
user is a member of are reachable (at first: every app; decision 101). Only
open issues are dedup candidates, so
the "closed recently — still happening?" flow goes. Code context is written
into the GitHub issue only, never shown in Discord.

## Model split

| Job | Model | Why |
|---|---|---|
| Issue type (bug / feature) | Jev `choice` | bounded, one of two |
| Pick the best of 3 LLM-written titles | Jev `choice` | select, don't generate |
| Which repo labels apply | Jev `noul` per label | independent, several can co-occur |
| Dedup stage 1: shortlist open issues | Jev `choice` over issues | reads every open issue's title + excerpt |
| Dedup stage 2: same issue? | Jev `score` per shortlisted issue | reads full bodies; 3 levels map onto match / ask / drop |
| Which code files and docs are relevant | Jev `choice` over a 40-file (code) / 10-file (docs) shortlist from keyword + embedding search | select from real paths; no hallucinated files; a doc never displaces the file to fix |
| Report → structured issue draft | GPT-6 Luna | generation |
| One-line summary per source file / doc (repo map) | GPT-6 Luna, background | generation |
| Notes for the chosen files | GPT-6 Luna | generation grounded in fetched code and docs |
| What a duplicate report adds | GPT-6 Luna | generation (unchanged behaviour) |

Models are pinned in config: `openai/gpt-6-luna` and `typesafe/jev-1.13`
(never the `~…-latest` aliases — Jev thresholds belong to the build they
were set on; startup rejects an aliased decision model).

## OpenRouter integration

Two thin typed `HttpClient`s (no .NET SDK exists; same reasoning as decision
6 for GitHub):

- **Chat** — `POST /api/v1/chat/completions` with `response_format:
  json_schema` (strict; schema generated from the C# DTO), `reasoning.effort`
  (default `medium` since decision 99; the repository map's summaries always
  `low`, decision 102), and `provider: { order: ChatProviders, allow_fallbacks:
  true, require_parameters: true }`. Default `ChatProviders` is
  `["openai/flex", "openai"]`: OpenRouter tries OpenAI's half-price flex
  endpoint first and falls through to the regular endpoint (then any other
  host) when flex answers 429/5xx. Flex can also *queue*, which is what got
  it reverted in decision 71, so interactive calls carry a client-side
  deadline (`ChatDeadlineSeconds`, default 30): past it, or on a transient
  error, the call is retried once on `RegularProviders` (`["openai"]`,
  regular tier). Background calls (repo map) get a long deadline instead.
- **Decisions** — `POST /api/alpha/decisions` with `{model, state,
  questions}`; answers are typed (`choice` / `noul` / `score`) and checked
  by `type`; a missing key or wrong type is an error, never a default.

Every response's `usage.cost` feeds a per-scope meter; each report logs its
total AI spend, which is how the budget below is verified in production.

## Report flow

```
/issue → modal (app dropdown if several apps) → submit
  ├─ download + sniff screenshots (unchanged)
  ├─ Jev: type (bug | feature)                          ┐ in parallel with
  ├─ Luna: draft {3 title candidates, body} per type    ┘ issue sync + labels fetch
  ├─ in parallel:
  │   ├─ Jev review: title choice + one noul per repo label
  │   └─ Jev dedup: stage 1 shortlist → stage 2 verify
  ├─ save pending report → Discord preview (type, labels, title, body)
  └─ in the background, while the reporter reads the preview (decision 98):
      code context — search terms → keyword + embedding shortlist → Jev picks
      (≤4 code files, ≤2 docs) → fetch them → Luna notes → saved on the draft
"Create issue" click
  ├─ upload screenshots (unchanged)
  ├─ create issue with the chosen labels, with the code context if it is ready
  ├─ answer the reporter, then announce
  └─ not ready (decision 105): in the background, await the running build (or
      build it after a restart) and edit the block into the issue body
```

### Titles
The draft prompt asks for three alternative titles that each name the
specific symptom (bugs) or capability (features) and where it happens, with
banned generic phrasings ("Bug", "Issue with the app", "Feature request").
Jev picks the one that best describes the body for someone scanning the
issue list. A Jev failure falls back to the first candidate.

### Labels
Labels are read live from the repo (`GET /labels`, paginated). Triage
outcomes a maintainer decides — `duplicate`, `invalid`, `wontfix`,
`good first issue`, `help wanted` — are never offered (per-app override:
`IgnoredLabels`). One `noul` per remaining label (cap 100), threshold 0.5.
The repo's canonical type label (`bug`; `enhancement`/`feature`) is always
added when it exists, so the type decision and the labels cannot disagree
about the basics.

### Dedup (open issues only)
The issue cache keeps **open** issues only (title, URL, first 2500
characters of the reporter's half of the body — the `MetaMarker` cut is
unchanged); incremental sync deletes rows that closed.

- **Stage 1 (recall):** one Jev `choice` per chunk of ≤120 open issues, each
  option an issue keyed `issue_<n>` with title + 300-char excerpt in state,
  plus `none`. Issues with probability ≥ 0.05 survive; with several chunks,
  a second `choice` over the survivors makes the probabilities comparable.
  Top 5 go on.
- **Stage 2 (precision):** one request, one `score` per shortlisted issue
  over the full excerpt: *different* / *related but distinct* / *same
  underlying problem*. `P(same) ≥ 0.5` → match; `≥ 0.2` → ask the reporter;
  else dropped. Exactly one match → "add my report"; several matches or any
  ask-band issue → pick list; nothing → straight to the draft.
- **Degradation:** stage 1 failing → no match (nothing to show); stage 2
  failing → ask the reporter over the stage-1 shortlist.

Thresholds are the Decisions skill's pre-probe defaults and are named
constants. No API key was available during implementation, so none of them
has been probed yet: `--dry-run owner/repo "text"` runs one report through
every model call (plus the code context) without storing or posting it, and
prints the decisions, the cost, and each decision's raw probabilities.

### Repository map + code context
- **Map:** per app, a SQLite table of source files and Markdown docs
  (`path`, blob SHA, ≤25-word summary of what the file does or the doc
  explains). A background worker checks the default branch's head at
  startup and then every 10 minutes; on a new commit it lists the tree, filters (code-extension allowlist plus
  `.md`/`.mdx`/`.markdown`/`.rst`/`.adoc`; vendor/build/`.github` directories,
  generated code, licences and codes of conduct out; ≤200 KB; cap 2000
  files), and summarizes only new or changed blobs (batched, flex). The map
  is marked complete only once every file is summarized; one check takes
  every changed file (batches of up to 25, four at once, each saved), so the
  startup check is the full first build, and a transient failure starts no
  further batches. Checks never overlap: a tick that finds one running is
  skipped (decision 103). A file the model refuses or skips is stored
  unsummarized so no check pays for it twice.
- **Retrieval** (decision 95/96): Luna writes developer search terms for the
  issue with the file tree in view; BM25 over path + summary and cosine over
  `voyageai/voyage-4` embeddings of path + summary each rank the files, fused
  by reciprocal rank; code and docs apart, top 40 code / top 10 docs.
- **Selection:** a Jev `choice` over each candidate list (same shortlist
  helper as dedup), keeping the top 4 code files and top 2 docs with
  probability ≥ 0.05.
- **Notes:** the chosen files' contents (≤12k chars each) plus the draft go
  to Luna, which returns per-file relevance notes ("Relevant code", "Related
  docs"). Paths it names that were not provided are dropped; if it fails, the
  picks are listed with their map summaries. The block is appended after the `MetaMarker`,
  so it never feeds dedup, with each link pinned to the commit its file was
  read at.
- **Never in Discord:** built in the background after the preview is shown
  and written only to GitHub, so a Discord reporter (any installer, now)
  cannot use the bot to read a private repo's code, and the preview does not
  wait for it. Any failure → the issue is created without it.

## Discord

- `/issue` (modal, no type), `/issue-install` (ephemeral user-install link:
  `https://discord.com/oauth2/authorize?client_id=<app id>&integration_type=1&scope=applications.commands`),
  `/list-issues [app]` (renamed from `/issues`, decision 100).
- Commands are **global** with `integration_types = [guild, user]` and
  `contexts = [guild, bot DM, private channel]`. Legacy per-guild
  registrations are overwritten with an empty set on ready.
- App resolution (`AppAccess`): a guild listed in some app's `GuildIds` sees
  those apps (as before); anywhere else (DMs, unconfigured servers via a user
  install) the user sees the apps of the configured servers they are a
  member of, checked per server over REST (decision 101).
- **Per-user rate limit** (`Limits:ReportsPerUserPerDay`, default 10, 0 =
  off): a user install lets anyone in a configured server report from
  anywhere, and each report costs money. Checked when `/issue` opens the modal, recorded on submit.

## Budget

Prices from the live catalog (2026-09-29): GPT-6 Luna flex $0.05/M in,
$0.25/M out (regular 2×); Jev 1.13 $0.042/M in, output free.

| Step | Tokens (typical) | Cost |
|---|---|---|
| Jev type | 1.5k in | $0.00006 |
| Luna draft (reasoning medium) | 2k in / 1.5k out | $0.0005 |
| Jev review (40 labels) | 4k in | $0.00017 |
| Jev dedup (200 open issues) | 28k in | $0.0012 |
| Code context (search terms, embedding, Jev over 50 candidates, notes), 70% of reports | ~6k Jev + ~26k/0.7k Luna | $0.0020 |
| **Per report** | | **≈ $0.0035** |

Originally estimated with Jev reading the whole map (≈ $0.0037 for an
800-file map, growing with repo size); live, that cost 179k Jev tokens on a
1,551-file repo, which is what decisions 95–96 replaced.

1000 reports ≈ **$3.5/month on flex**, ≈ $5 if every call fell back to
the regular tier; repo-map upkeep adds cents (a 1000-file first build is
≈ $0.15, afterwards only changed files). The hard cap belongs on the
OpenRouter key's credit limit; the per-user limit stops one account from
spending it.

## Data

- `CachedIssue` replaces `IssueEmbedding` (no vectors).
- `PendingReport` gains `LabelsJson`.
- New: `RepoFile`, `RepoMapState`.
- The schema is stamped with `PRAGMA user_version`; a mismatch drops and
  recreates the database (everything in it is a rebuildable cache or a
  draft younger than an hour) — replacing "delete your SQLite file by hand".

## Removed

`text-embedding-3-small`, `VectorRanker`, the float-BLOB converter,
`Microsoft.Extensions.AI*`, the `OpenAI` package and config section,
`/report-issue`, `/request-feature`, and the closed-issue
"still happening?" flow.
