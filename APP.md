# APP.md

DiscordGithubBot is a .NET 10 console application that lets the users of one
or more apps report bugs and request features directly from Discord, without
ever needing a GitHub account. It turns a short Discord message into a
well-written, deduplicated, labelled GitHub issue (or a comment on an existing
one) that names the code and docs it is about. Every model call goes through
[OpenRouter](https://openrouter.ai/): TypeSafe's **Jev** decision model makes
every judgment, **GPT-6 Luna** writes every piece of text, and a human always
confirms before anything is written to GitHub. For the design see
`docs/superpowers/specs/2026-09-29-openrouter-jev-redesign.md` (and the
original `docs/superpowers/specs/2026-08-18-discord-github-issue-bot-design.md`);
for why specific choices were made, see `docs/DECISIONS.md`.

## What it does

A Discord server admin configures one or more "apps," each pointing at a
GitHub repository, a set of Discord guilds and the channels where new issues
are announced. Anyone who can run the bot's commands — in those guilds, or
anywhere once they have added the bot to their own Discord account — runs
`/issue`, describes the problem and attaches screenshots. The bot decides
whether it is a bug or a feature request, drafts the issue, checks it against
the repository's open issues, picks the repository's labels, and — after the
reporter confirms — creates the issue (with the relevant code and docs
attached) or comments on the matching one. New issues are also announced
publicly in the app's configured channel(s).

## The report workflow

1. **Modal.** The reporter runs `/issue` — no options. A modal opens with a
   multiline description field and an optional file upload for up to 10
   screenshots; when more than one app is reachable from where the command
   was run, a required "App" dropdown sits on top of the form (see "Where the
   bot works" below). A reporter over the daily report limit is told when
   they can report again instead of getting the modal.
2. **Defer, then immediate download.** On submit, the bot acknowledges the
   interaction ephemerally (Discord allows three seconds) and downloads any
   attached image bytes right away — Discord's CDN URLs expire in about 24
   hours. Anything that is not an image, is larger than 10 MB, or fails to
   download is skipped and listed by name in a warning line above the
   reply. "Not an image" is decided on the downloaded bytes — their magic
   numbers must say PNG, JPEG, GIF, or WebP — not on the type Discord
   declares, which the uploading client controls. Everything from here on
   stays private to the reporter until an issue is actually created.
3. **Type (Jev).** A two-option decision: is this a *bug* (something that
   should work is broken) or a *feature request* (something new or
   different)? It picks the draft template and the type label. If the call
   fails, the report is treated as a bug; the preview shows the type either
   way.
4. **Draft (GPT-6 Luna).** The raw text becomes a structured body following a
   bug template (Description / Steps to Reproduce / Expected / Actual) or a
   feature template (Summary / Motivation / Proposed Solution), translated
   into English if needed, plus **three alternative titles**. The prompt holds
   titles to what the issue actually is: the specific symptom or capability
   and where it happens, in the reporter's concrete details, never a generic
   "Bug report" or "Issue with the app". Nothing is invented; a section with
   nothing to say is left out. A draft that fails twice ends the flow with an
   apology rather than a half-written issue.
5. **Review (Jev), in parallel with dedup.** One decision request picks the
   title that best describes the body for someone scanning the issue list,
   and asks, label by label, whether each of the repository's own labels
   applies (read live from GitHub). Triage labels — `duplicate`, `invalid`,
   `wontfix`, `won't fix`, `good first issue`, `help wanted` — are never
   offered (per-app `IgnoredLabels` replaces that list), and the repository's
   own type label (`bug`; `enhancement`/`feature`/`feature request`) is always
   attached when it exists.
6. **Duplicate check (Jev), against open issues only.** The bot keeps a cached
   copy of the repository's open issues (incremental sync per report, full
   resync daily). Jev first reads every open issue's title and opening — in
   chunks of 120 when there are many — and shortlists the few that could be
   the same problem; then it lays each shortlisted issue's full text next to
   the draft and judges it *different*, *related but distinct*, or *the same
   underlying problem*. Issues the bot filed itself are compared on the
   reporter's half of the body only — everything after a hidden marker
   (footer, screenshots, code context) is cut first, so two reports never look
   alike merely for both having come through Discord.
7. **Outcome** (all ephemeral):
   - exactly one issue is probably the same → **"Same issue — add my report"**
     or **"Not it — show my draft"**;
   - several are, or one might be → a select menu of candidates plus
     **"None of these — new issue"**;
   - nothing → straight to the draft preview.
8. **Preview and confirm.** Every path that creates an issue shows the draft
   first — with the classified type and the chosen labels in small print
   above the title — behind **Create issue** / **Cancel**. The duplicate path
   confirms against the matched issue instead.
9. **Create or comment.** The **code context** (see below) was started in the
   background the moment the preview appeared, so it is usually ready by the
   time the reporter clicks; if not, the click waits for the rest of it while
   the screenshots upload to GitHub. The body then gets
   a `### Screenshots` gallery, the "Relevant code" / "Related docs" block,
   and a `_Created by **<name>** in Discord server **<server>**._` footer
   (`via Discord` when the server is unknown — a DM, or a server the bot is
   not in). The issue gets the chosen labels, a public announcement posts in
   the app's channel(s), and the reporter gets an ephemeral confirmation. On
   comment: screenshots upload the same way, and the comment carries only
   what the report adds to the issue (GPT-6 Luna compares the draft with the
   cached issue text) above an `_Also reported by …_` footer — or that footer
   alone when it adds nothing, or the full draft if the comparison fails.
   Nothing posts publicly on the comment path. A screenshot that cannot be
   uploaded is named in a note rather than blocking the issue.

Between the modal submit and that final click the draft (with the downloaded
screenshot bytes and the chosen labels) lives in SQLite for **one hour**; an
hourly background pass sweeps expired rows, and a click on an older message
answers "that report is no longer waiting". Clicking a button also strips the
buttons off the message it was clicked on, and the confirming click claims
the draft in the database before it touches GitHub, so the same report cannot
be filed twice. A confirmation that fails — GitHub down, say — gives the claim
back, so the draft and its buttons still work for a retry.

## Code and docs context

Created issues name the files they are most likely about, so a maintainer
starts from the right place:

- **Repository map.** A background worker keeps, per app, one line per source
  file and Markdown doc (`.md`, `.mdx`, `.markdown`, `.rst`, `.adoc`) on the
  default branch: what the file is responsible for, or what the doc explains,
  written by GPT-6 Luna on the flex tier. Dependencies, build output,
  generated or minified code, assets, lock files, `.github/`, licences and
  codes of conduct are left out, as is anything over 200 KB; a repository with
  more than 2000 such files keeps the shallowest 2000. The worker checks
  every app **at startup and then every 10 minutes**, on a background thread:
  it asks GitHub for the default branch's head (two calls when nothing
  changed) and summarizes every file added or changed since the last check —
  compared by git blob SHA, so an edit is never missed and an unchanged file
  is never paid for twice — and drops deleted ones. One check takes all the
  changed files, however many, so the startup check is the whole first build.
  Checks never overlap: a 10-minute tick that finds the previous check still
  running is skipped (and logged), and the next tick tries again. Files go to
  the model in batches of up to 25 (60k characters), four batches at once,
  each saved as it lands; file contents are fetched eight at a time, and
  summaries run at reasoning `low` whatever `ReasoningEffort` says. (One batch
  at a time, at `medium`, with one fetch at a time, mtg-grimoire's ~1,550
  files took about 35 minutes.) A request OpenRouter refuses outright
  (credits exhausted, bad key) or a transient failure starts no further
  batches and loses nothing already saved; the next check picks up where it
  stopped. Reports filed within 10 minutes of a push can still see the
  previous version of the files it changed.
- **Embeddings.** Every summary (with its path) is also embedded with
  `voyageai/voyage-4`, so files can be found by meaning, not only by shared
  words. A changed summary gets a new vector; each check embeds whatever lacks
  one — which is also how an existing map, or a switch of embedding model,
  catches up (a 1,551-file map cost $0.0035 to embed).
- **While the reporter reads the preview.** The code context is for
  maintainers, so the preview does not wait for it: it starts in the
  background as soon as the preview is shown and is saved with the draft. On
  "Create issue" it is usually ready (measured: the preview in ~5 s, the code
  context ~20–25 s later on flex), so creating takes a few seconds; a
  reporter who clicks sooner waits only for the remainder, and a draft that
  outlived a bot restart gets its code context built at the click. It is built
  for every draft, duplicates and cancelled ones included (~$0.002 each).
  Reporters rarely use the code's vocabulary ("nothing comes up" rather than
  `fts_query`), so finding the files is two-sided:
  1. GPT-6 Luna writes the search terms a developer would use, looking at the
     repository's file tree so the terms match the names this codebase uses;
  2. keyword search (BM25 over path + summary) and embedding search each rank
     the files, and the two rankings are fused; code and docs are ranked
     apart, and the top 40 code files and top 10 docs go on;
  3. Jev picks up to four code files and two docs from those candidates;
  4. the bot reads them at the exact version the map summarized, and GPT-6
     Luna says which are really involved and how, naming functions or
     documented behaviour, plus a short note on where a fix would likely go.

  Measured on mtg-grimoire's history (decision 95), this finds a file the fix
  actually changed in the top 10 for 87% of user-worded reports, and Jev
  choosing from the 40 candidates matched Jev reading the whole map while
  using about 3% of the tokens. Without search terms the report text is
  searched alone; without an issue embedding the keyword ranking stands
  alone. The result is appended to the issue under "Relevant code" and
  "Related docs", each link pinned to the commit its file was read at (so it
  shows exactly the version the notes describe), and a line saying it is
  AI-generated.
- **GitHub only.** The block is never shown in Discord: the preview is the
  reporter's text, and anyone who can run the bot is not necessarily allowed
  to read a private repository's code. The map covers the issue repository
  itself (`Repo`), so the notes are exactly as visible as the code they
  describe.
- **Degradation.** If the notes cannot be written, the picked files are listed
  with their map summaries, marked as unread; if selection fails, or the map
  is not built yet, the issue is filed without the block.

## Slash commands

- **`/issue`** — opens the report modal described above.
- **`/issue-install`** — an ephemeral message with an **Add to my account**
  button: the Discord link that installs the bot on the caller's own account.
- **`/list-issues [app]`** — an ephemeral list of the target repo's open issue
  titles with links, capped at 25 (and at what fits Discord's message size,
  whole lines only) with a "+K more on GitHub" note for the rest.

`/issue` takes no options: with one reachable app the modal opens straight
away, with several the modal asks which via a dropdown. `/list-issues` keeps an
`app` option (it opens no modal to ask in), needed only when several apps are
reachable; an unknown name answers with the valid ones.

## Where the bot works (user install)

Every command is registered **globally** and can be used in servers, in DMs
with the bot, and in group DMs — both when the bot was added to a server and
when a user added it to their own account. Which apps a command offers
depends on where it was run:

- In a server listed in some app's `GuildIds`: exactly those apps (as
  before).
- Anywhere else — a DM, a group DM, or a server nobody configured: the apps
  of the configured servers **the user is a member of**. Someone in none of
  them is told so; installing the bot does not open every repository to
  anyone who finds it.

Membership is asked of Discord's REST API (`GET /guilds/{id}/members/{user}`,
one call per configured server, in parallel) each time, not read from the
gateway cache: the bot runs with the `Guilds` intent only, so its cache never
hears that someone left or was banned. That endpoint needs no privileged
intent, but it only works for servers the bot is in — a configured server
the bot has left is logged as a warning and its apps are skipped. `/issue`
must show its modal within Discord's three seconds, so the lookups get two
seconds together; a server Discord did not answer for in time only drops its
own apps, and if that leaves none the reply asks the user to try again rather
than saying they are in no server. The modal submit checks again (the pick
echoes back through the client, and the reporter may have left since).

To allow user installs, open the application in the [Discord Developer
Portal](https://discord.com/developers/applications) → **Installation** →
tick **User Install** (scope `applications.commands`). Keep **Guild Install**
for the servers the bot joins (scopes `bot` + `applications.commands`). The
link `/issue-install` hands out is built from the interaction's application id,
so there is nothing to configure in the bot. Global commands replace the
per-server registrations of earlier versions; the bot clears those on start.

Because every report costs money and anyone in a configured server can
report from anywhere once they install the bot, each Discord user may submit **10 reports per
rolling 24 hours** by default (`Limits:ReportsPerUserPerDay`, `0` = no cap).
The limit is checked when `/issue` opens the modal and counted on submit; it
lives in memory, so a restart resets it.

## Models and cost

| Model | Via | Used for |
| --- | --- | --- |
| `typesafe/jev-1.13` | OpenRouter Decisions API | type, title choice, labels, duplicate shortlist + verification, file and doc selection |
| `openai/gpt-6-luna` | OpenRouter chat completions | the draft, repository-map summaries, search terms, code notes, duplicate comments |
| `voyageai/voyage-4` | OpenRouter embeddings | repository-map summaries and each new issue, for finding its files by meaning |

**Flex first — except the draft.** Chat calls send `provider.order =
["openai/flex", "openai"]` (`ChatProviders`): OpenAI's half-price flex tier
first, the regular tier behind it, and OpenRouter itself moves on when flex
rejects a request. Flex can also queue, so a call a reporter is waiting on has
`ChatDeadlineSeconds` (30) on the first attempt; past that, or after any
transient failure, it is retried once on `RegularProviders` (`["openai"]`,
the regular tier). The **draft** goes to `RegularProviders` directly: the
reporter waits on it before seeing anything, and flex more than doubled it
(7.2 s against 3.2 s measured), for a saving of about $0.0002 a report.
Search terms, code notes, duplicate comments and the repository map stay on
flex; background work waits out the queue.

**Reasoning effort** is `medium` for every GPT-6 Luna call except the
repository map's one-line summaries (always `low`), set by
`OpenRouter:ReasoningEffort` (env var `OpenRouter__ReasoningEffort`): one of
`none`, `minimal`, `low`, `medium`, `high`, `xhigh`, `max`, or empty for the
model's own default; anything else fails startup. Read side by side on real
reports, `low` and `medium` wrote equally good drafts and code notes (`medium`
fills in reproduction steps a little more often; drafts ~4.0 s against
~3.3 s on the regular tier), while `none` skipped bug-template sections and
failed one code-notes call in four.

**Pinned models.** `DecisionModel` must be a versioned id — startup rejects a
`~…-latest` alias, because every decision threshold belongs to one build.
When changing models, re-check the thresholds with `--dry-run`.

### Cost

Prices from OpenRouter's catalog on 2026-09-29: GPT-6 Luna flex $0.05 per
million input tokens / $0.25 per million output (regular 2×), Jev 1.13 $0.042
per million input tokens with free output.

| Per report (typical) | Cost |
| --- | --- |
| Jev: type | $0.00006 |
| Luna: draft (reasoning medium) | $0.0005 |
| Jev: title + 40 labels | $0.0002 |
| Jev: duplicate check over 200 open issues | $0.0012 |
| Code context — every draft, built in the background: search terms, issue embedding, Jev picks (~6k tokens), notes | $0.0020 |
| **Total** | **≈ $0.0035** |

1000 reports a month come to about **$3.50** on flex, about $5 if every chat
call fell back to the regular tier, and code context no longer grows with the
size of the repository. Keeping the repository map current adds cents (a
first build of mtg-grimoire's 1,551 files was $0.19 of summaries plus $0.004
of embeddings; after that only changed files are paid for). Measured on
mtg-grimoire: a created issue used $0.0019 of GPT-6 Luna and 9,631 Jev input
tokens (≈ $0.0004), where reading the whole map had cost 179k Jev tokens.

The bot logs the AI usage of every drafted report, created issue and map
refresh as `$<cost> in <n> call(s), <k> decision-model input tokens`.

**BYOK.** With your own provider keys behind OpenRouter ("bring your own
key"), OpenRouter's reported `cost` is only its own fee — usually $0 — and the
provider bills you directly. Chat responses still say what OpenAI charged
(`cost_details.upstream_inference_cost`), and the bot counts that. Decision
responses report no upstream cost, so for Jev the logs show input tokens
instead: TypeSafe bills those at $0.042 per million.

**Hard caps.** Without BYOK, set a monthly credit limit on the OpenRouter key.
With BYOK that limit only caps OpenRouter's fees — set the budget limits on
the OpenAI and TypeSafe accounts instead. The per-user report limit only stops
one account from spending the budget.

### Tuning the decisions

Every decision gate — labels at P ≥ 0.5, duplicates offered at P(same) ≥ 0.5
and asked about at ≥ 0.2, shortlist floors at 0.05 — is a default set before
any real data was seen. Run representative reports through

```
dotnet run --project src/DiscordGithubBot -- --dry-run owner/repo "the report text"
```

It refreshes the app's repository map, then prints the type, every drafted
title with the chosen one marked, the labels, the duplicate verdict with its
shortlist, the body, the code context and the AI cost — and, above that, each
Jev decision's raw probabilities (Debug log). It stores no report and posts
nothing to Discord or GitHub (it does update the issue cache and the map).
Adjust the named constants in `DuplicateFinder`, `DraftReviewer`,
`CodeContextBuilder` if a clear case lands on the wrong side of a gate.

## Logs

Every line is one line (`Logging:Console:FormatterOptions:SingleLine`), and
what the bot does is logged at Information in its own words; the HTTP client
factory's four lines per request and Entity Framework's SQL are off
(`System.Net.Http.HttpClient` and `Microsoft` log at Warning, so their
failures still show). At startup the bot logs its models, database, and every
app with its auth mode, servers and channels (never a secret), then "Connected
to Discord as …". After that, expect:

- one line per interaction: `Handled /issue from alice (123) in server Foo
  (456) in 0.4 s.` — or why it failed, or why the user was turned away (no
  app available, daily limit);
- per report, keyed by an eight-digit report id: `Drafted Bug report 1a2b3c4d
  … "title", labels …; no duplicate …`, then `Code context for report
  1a2b3c4d … ready`, then `Created issue #684 … <url>`, `Commented on …` or
  `Report 1a2b3c4d was cancelled` — each with its AI usage;
- per repository-map check that has work: how many files are new or changed,
  progress every 100 files, how many were summarized, then one summary line
  with its duration and cost — or that a tick was skipped because the
  previous check is still running.

To see more, raise a category with an environment variable, e.g.
`Logging__LogLevel__DiscordGithubBot=Debug` (every model call with its
tokens, cost and duration; every map batch) or
`Logging__LogLevel__System.Net.Http.HttpClient=Information` (every HTTP
request).

## Configuration

Configuration layers in order, last one wins: `appsettings.json` →
`appsettings.{Environment}.json` → environment variables (`__` as the
nesting delimiter) → command-line arguments → Docker secrets at
`/run/secrets` (via `Microsoft.Extensions.Configuration.KeyPerFile`, added
last and only when that directory exists, so where they exist they always
win).

The JSON shape (see `src/DiscordGithubBot/appsettings.json` for the
checked-in defaults, and `.env.example` for the env-var form of every knob):

```json
{
  "Discord": { "Token": "<secret>" },
  "OpenRouter": {
    "ApiKey": "<secret>",
    "ChatModel": "openai/gpt-6-luna",
    "ChatProviders": ["openai/flex", "openai"],
    "RegularProviders": ["openai"],
    "ChatDeadlineSeconds": 30,
    "ReasoningEffort": "medium",
    "DecisionModel": "typesafe/jev-1.13",
    "EmbeddingModel": "voyageai/voyage-4"
  },
  "Database": { "Path": "db/app.db" },
  "Limits": { "ReportsPerUserPerDay": 10 },
  "Apps": [
    {
      "Name": "MyApp",
      "Repo": "owner/repo",
      "GitHubToken": "<secret>",
      "GuildIds": [111111111111111111],
      "ChannelIds": [222222222222222222],
      "IgnoredLabels": ["duplicate", "wontfix"]
    }
  ]
}
```

`ChatProviders`, `RegularProviders` and `IgnoredLabels` fall back to their
defaults when absent; a configured list replaces the default rather than
extending it. `Database:Path` is relative to the working directory
(`db/app.db` by default); the folder is created at startup if it does not
exist. The database only holds caches and hour-long drafts: its schema is
stamped: a file from an older build is upgraded in place when the change is
additive (new columns — the repository map survives), and rebuilt otherwise,
rather than failing.

`Apps` is a list, not a dictionary — `owner/repo` contains a `/`, which can't
appear in an env-var name, so a list with a unique `Repo` field stays
overridable. Startup fails fast — every problem printed as a
`CONFIG ERROR: <key> …` line on stderr, exit code 1, before anything
connects — if: the Discord token, OpenRouter key, chat, decision or embedding
model, or database path is missing; the decision model is a `~` alias; the chat
deadline is not positive or the report limit is negative; there are no apps;
or any app has an empty name, a `Repo` that isn't `owner/repo`, a repo that
another app already claims (case insensitive), no guild ids, no channel ids,
or credentials that are not exactly one of the two forms described next. A
leftover `OpenAI` section from an older build prints a warning.

### GitHub credentials: PAT or GitHub App

Every app authenticates one of two ways, and **exactly one** — configuring
both, or neither, is a startup error naming the app:

- **`GitHubToken`** — a personal access token. Issues are authored by the
  human who owns the token.
- **`GitHubApp`** — a GitHub App installation. Issues are authored by
  `<app-name>[bot]`, which is usually what you want for a shared bot: the
  identity is the bot's own, it does not vanish when a person leaves, and
  permissions are scoped to the repositories the App is installed on rather
  than to everything the token owner can reach.

```json
{
  "Name": "MyApp",
  "Repo": "owner/repo",
  "GitHubApp": {
    "AppId": 123456,
    "InstallationId": 987654321,
    "PrivateKey": "-----BEGIN RSA PRIVATE KEY-----\n…\n-----END RSA PRIVATE KEY-----"
  },
  "GuildIds": [111111111111111111],
  "ChannelIds": [222222222222222222]
}
```

Setting one up:

1. **Create the App** — GitHub → Settings → Developer settings → GitHub Apps
   → *New GitHub App*. A personal App is fine; an organization-owned one is
   better if the repositories belong to an org. Webhooks are not used: untick
   *Active*.
2. **Permissions** — Repository permissions → **Issues: Read and write**
   (creating issues and comments, reading labels) and **Contents: Read and
   write** (reading the code and docs for the repository map, and the tier-2
   screenshot fallback commits to the `issue-assets` branch). Nothing else is
   needed.
3. **Install it** — the App's *Install App* tab → install on the account that
   owns the repositories, and select the repositories you configure as apps.
4. **Note the ids** — the **App ID** is on the App's *General* tab. The
   **Installation ID** is the last path segment of the URL you land on after
   installing, `…/settings/installations/<InstallationId>` (or, for an org,
   `…/organizations/<org>/settings/installations/<InstallationId>`).
5. **Generate a private key** — *General* → *Private keys* → *Generate a
   private key*. GitHub downloads a `.pem` once; it cannot be re-downloaded.
   Supply it as **either** `PrivateKey` (the PEM text itself) **or**
   `PrivateKeyPath` (a path to the file) — again, exactly one. Both are
   checked at startup: a path that does not exist, and key bytes that RSA
   cannot import, each fail the run rather than the first report.

With Docker secrets the natural form is `PrivateKey`, because key-per-file
maps a file's *content* to the config key its *name* spells: a secret file
named `Apps__0__GitHubApp__PrivateKey` whose content is the PEM binds
directly, no path involved. `PrivateKeyPath` is for setups that mount the key
somewhere of their own choosing —
`Apps__0__GitHubApp__PrivateKeyPath=/run/keys/app.pem` — and for local runs
where the `.pem` sits on disk. Both PKCS#1 (`BEGIN RSA PRIVATE KEY`, what
GitHub hands out) and PKCS#8 PEM are accepted.

What does *not* work is inlining the PEM into an environment variable. A PEM
is multi-line; the `\n` in the JSON above is a real newline once JSON is
parsed, but the same two characters exported from a shell stay two characters
and the key will not import. Use `PrivateKeyPath` (or a secret file) whenever
the configuration comes from the environment.

Nothing else changes: the bot mints an installation access token when it
needs one, caches it for the hour GitHub gives it, and re-mints shortly
before it expires. There is one caveat, and it is the screenshot upload —
see the smoke test below.

## Running locally

Copy `.env.example` to `.env`, fill in real values, and start the bot with
`./run.ps1` (PowerShell) — it takes the same arguments as the app, so
`./run.ps1 --dry-run owner/repo "text"` works too. The app itself does not read
`.env` files (only compose does); `run.ps1` loads `.env` into its own process
environment, skipping empty values so an unfilled line never overrides a
setting made elsewhere, picks up bot settings saved as Windows user variables
after the terminal was opened, and points the content root at the project so
`appsettings.json` is actually loaded. `dotnet run --project src/DiscordGithubBot`
still works when the settings are already in the environment. `appsettings.json`
ships with safe, secret-free defaults, so local runs just need the Discord
token, the OpenRouter key, and per-app GitHub credentials from elsewhere. For
an app on GitHub App credentials that means `Apps__0__GitHubApp__PrivateKeyPath`
pointing at the `.pem` on disk — an exported environment variable cannot
carry the PEM's newlines, and startup rejects the mangled key.

`dotnet build` builds everything, `dotnet test` runs the suite.
`--dry-run owner/repo "text"` (see "Tuning the decisions") runs one report
through every model call without Discord.

To check that image uploads work against a real repository without going
through Discord:

```
dotnet run --project src/DiscordGithubBot -- --smoke-upload owner/repo
```

It uploads a 1×1 PNG with the same code path a report uses and exits without
starting the gateway. `owner/repo` must be one of the configured apps (its
own credentials are what get used), and the rest of the configuration must
still validate. It first prints which credentials it is running under —
`Smoke upload to owner/repo — auth: PAT` or
`… auth: GitHub App (installation token)` — then the result:
`SMOKE OK: <url>` (exit 0) or `SMOKE FAILED: both tiers failed` (exit 1). A
`github.com/user-attachments/…` URL means the unofficial endpoint accepted
the upload, a `raw.githubusercontent.com/…/issue-assets/…` URL means it did
not and the Contents-API fallback did the work (the fall-through is also
logged as a warning).

That distinction matters most for GitHub App credentials. The tier-1
`user-attachments` endpoint is the undocumented one behind the web UI's
drag-and-drop (decision 3), so whether it accepts an installation token is
not something the documentation answers — this smoke run is what answers it.
If it does not, screenshots still work: they land on the `issue-assets`
branch through the official Contents API instead, which is why the App needs
**Contents: Read and write**.

## Running in Docker

`docker compose up --build` runs the bot in a container: the image is a
multi-stage build (SDK to publish, runtime to run) that runs as the image's
non-root `app` user and reads `Database__Path=/data/app.db`, with the named
`botdata` volume mounted at `/data` so the SQLite file survives rebuilds.

Secrets reach the container two ways, and you can mix them:

- **`.env`** — copy `.env.example` to `.env` and fill it in; compose loads it
  if it exists and ignores it if it doesn't (`required: false`). Every key in
  `.env.example` works here, including `Apps__0__GitHubToken`, with one
  exception: **leave `Database__Path` out of a `.env` used with compose**
  (it ships commented out for exactly this reason). A `.env` sets container
  environment variables, which override the image's
  `ENV Database__Path=/data/app.db`, so a relative `db/app.db` would put the
  database under the root-owned `/app` — the non-root `app` user cannot
  create that folder, the container dies on startup and `restart:
  unless-stopped` turns it into a crash loop. If you do want the key in
  `.env`, it must read `Database__Path=/data/app.db` so it still lands on
  the `botdata` volume.
- **Docker secrets** — files under `secrets/` (gitignored), mounted at
  `/run/secrets` and read key-per-file, so they win over everything else.
  The file *name* is the config key with `__` for nesting:
  `secrets/Discord__Token`, `secrets/OpenRouter__ApiKey`, and, if you want the
  per-app GitHub PATs out of `.env` too, `secrets/Apps__0__GitHubToken`
  (one file per app index) — add each new file to both the service's
  `secrets:` list and the top-level `secrets:` block.

  A GitHub App private key belongs here rather than in `.env`: drop the
  downloaded `.pem` at `secrets/Apps__0__GitHubApp__PrivateKey` (the file
  name is the key, its content is the PEM — no `PrivateKeyPath` needed, and
  no way to get the newlines wrong in an env var). `docker-compose.yml`
  ships that secret commented out; uncomment both halves to use it. The
  numeric `AppId` and `InstallationId` are not secret and can stay in `.env`.

Compose only creates the secret files it is told about, and it **fails to
start if a referenced secret file is missing**. So the shipped
`docker-compose.yml` is a starting point, not a requirement: if you keep
everything in `.env`, delete the service-level `secrets:` list and the
top-level `secrets:` block entirely.

## CI/CD

Two GitHub Actions workflows live in `.github/workflows/`:

- **CI** (`ci.yml`) — every pull request and every push to `main` runs
  `dotnet restore` / `build` / `test` on Ubuntu with .NET 10.
- **Release** (`release.yml`) — every push to `main` re-runs the tests and,
  only if they pass, builds the Docker image from the repository `Dockerfile`
  and pushes it to GitHub Packages as `ghcr.io/<owner>/<repo>` tagged
  `latest` and `sha-<commit>` (linux/amd64). It authenticates with the
  workflow's own `GITHUB_TOKEN` — no secrets to configure. The first push
  creates the ghcr.io package as **private**; flip it to public in the
  package settings if the image should be pullable without a token.

## Manual verification

The suite covers the logic; these steps cover the parts only a live bot can
prove. Run the bot with a real Discord token, a real OpenRouter key and real
GitHub credentials for a test repository that has a few open issues, a few
labels (say `bug`, `enhancement`, `ui`, `android`) and some source files and
Markdown docs, in a guild where that repository is the guild's **only**
configured app. Enable **User Install** in the Developer Portal first.

1. **Dry run.** `dotnet run --project src/DiscordGithubBot -- --dry-run owner/repo "…"`
   with a clear bug, a clear feature request, a near-copy of an open issue and
   an off-topic message. Check the type, the chosen title (specific, not
   generic), the labels, the duplicate verdict and the code context, and read
   the raw probabilities in the Debug lines. The first run builds the
   repository map — expect its summary line and a few cents of cost.
2. **Modal.** Run `/issue` in the guild. The modal opens immediately, with no
   "App" dropdown. Describe a bug, attach two screenshots, submit.
3. **New issue path.** The reply is the draft preview: "Bug report · Labels:
   …" in small print, a specific title, the body, **Create issue** /
   **Cancel**. Press *Create issue* and check that the issue exists on GitHub
   with both screenshots inline, the previewed labels, a "Relevant code" (and,
   if a doc fits, "Related docs") block linking to real files at a commit,
   and the `_Created by …_` footer; that a public announcement appears in the
   app's channel(s); and that everything in the command channel was
   ephemeral. The log line for the creation states the AI cost.
4. **Feature path.** Run `/issue` with a feature request; the preview says
   "Feature request" and uses the Summary / Motivation / Proposed Solution
   template.
5. **Duplicate path.** Report the step-3 bug again in different words. The
   reply should be "This looks like an existing issue: #N …" with **Same
   issue — add my report** / **Not it — show my draft**. Add the report and
   confirm the comment on #N carries only what the second report added (or
   just the "Also reported by …" line), and that nothing is announced.
6. **Closed issues are ignored.** Close #N on GitHub and report the same bug a
   third time: it now goes straight to a draft preview.
7. **User install.** Run `/issue-install`, press **Add to my account**, and
   authorize. In a DM with a friend (or any server without the bot), `/issue`
   is available and offers only the apps of the configured servers you are
   in (a dropdown when that is several). The created issue's footer reads
   "via Discord". From an account in none of the configured servers,
   `/issue` answers that you're not in any of them instead of opening.
8. **Rate limit.** With `Limits__ReportsPerUserPerDay=1`, a second `/issue`
   within a day answers with the time you can report again instead of a modal.
9. **`/list-issues`.** The ephemeral list shows the repository's open issues
   with working links; `/issues` is gone from the command list.
10. **Image-upload smoke test and GitHub App.** As before: `--smoke-upload
    owner/repo` must print `SMOKE OK: <url>`; then swap the PAT for a
    `GitHubApp` block, restart, run the smoke test again (first line must say
    `auth: GitHub App (installation token)`), repeat step 3, and confirm the
    issue is authored by `<app-name>[bot]` and the code context still
    appears (the App needs Contents: Read).
