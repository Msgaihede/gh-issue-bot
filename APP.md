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
9. **Create or comment.** On create, in parallel: the screenshots upload to
   GitHub, and the **code context** is built (see below). The body then gets
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
  more than 2000 such files keeps the shallowest 2000. The worker checks each
  branch head hourly (two GitHub calls when nothing changed) and summarizes
  only files whose git blob changed. A first build is capped at 400 files per
  pass and resumes every five minutes until the map is complete. A request
  OpenRouter refuses outright (credits exhausted, bad key) just ends the pass;
  the map picks up where it stopped once the key works again.
- **At "Create issue".** Jev picks up to four code files and, separately, up
  to two docs from the map; the bot reads them at the exact version the map
  summarized; GPT-6 Luna says which are really involved and how, naming
  functions or documented behaviour, plus a short note on where a fix would
  likely go. The result is appended to the issue under "Relevant code" and
  "Related docs", each link pinned to the commit its file was read at (so it
  shows exactly the version the notes describe), and a line saying it is
  AI-generated.
- **GitHub only.** The block is never shown in Discord — anyone who can run
  the bot is not necessarily allowed to read a private repository's code — and
  it is built only when an issue is actually created, so duplicates and
  cancelled drafts cost nothing. The map covers the issue repository itself
  (`Repo`), so the notes are exactly as visible as the code they describe.
- **Degradation.** If the notes cannot be written, the picked files are listed
  with their map summaries, marked as unread; if selection fails, or the map
  is not built yet, the issue is filed without the block.

## Slash commands

- **`/issue`** — opens the report modal described above.
- **`/issue-install`** — an ephemeral message with an **Add to my account**
  button: the Discord link that installs the bot on the caller's own account.
- **`/issues [app]`** — an ephemeral list of the target repo's open issue
  titles with links, capped at 25 (and at what fits Discord's message size,
  whole lines only) with a "+K more on GitHub" note for the rest.

`/issue` takes no options: with one reachable app the modal opens straight
away, with several the modal asks which via a dropdown. `/issues` keeps an
`app` option (it opens no modal to ask in), needed only when several apps are
reachable; an unknown name answers with the valid ones.

## Where the bot works (user install)

Every command is registered **globally** and can be used in servers, in DMs
with the bot, and in group DMs — both when the bot was added to a server and
when a user added it to their own account. Which apps a command offers
depends on where it was run:

- In a server listed in some app's `GuildIds`: exactly those apps (as
  before).
- Anywhere else — a DM, a group DM, or a server nobody configured: **every**
  configured app.

To allow user installs, open the application in the [Discord Developer
Portal](https://discord.com/developers/applications) → **Installation** →
tick **User Install** (scope `applications.commands`). Keep **Guild Install**
for the servers the bot joins (scopes `bot` + `applications.commands`). The
link `/issue-install` hands out is built from the interaction's application id,
so there is nothing to configure in the bot. Global commands replace the
per-server registrations of earlier versions; the bot clears those on start.

Because every repository is reachable by anyone who installs the bot, and
every report costs money, each Discord user may submit **10 reports per
rolling 24 hours** by default (`Limits:ReportsPerUserPerDay`, `0` = no cap).
The limit is checked when `/issue` opens the modal and counted on submit; it
lives in memory, so a restart resets it.

## Models and cost

| Model | Via | Used for |
| --- | --- | --- |
| `typesafe/jev-1.13` | OpenRouter Decisions API | type, title choice, labels, duplicate shortlist + verification, file and doc selection |
| `openai/gpt-6-luna` | OpenRouter chat completions | the draft, repository-map summaries, code notes, duplicate comments |

**Flex first.** Chat calls send `provider.order = ["openai/flex", "openai"]`:
OpenAI's half-price flex tier first, the regular tier behind it, and
OpenRouter itself moves on when flex rejects a request. Flex can also queue,
so a call a reporter is waiting on has `ChatDeadlineSeconds` (30) on the first
attempt; past that, or after any transient failure, it is retried once on
`ChatRetryProviders` (`["openai"]`, regular tier). Background work (the
repository map) waits out the queue. Reasoning effort defaults to `low`.

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
| Luna: draft (reasoning low) | $0.0004 |
| Jev: title + 40 labels | $0.0002 |
| Jev: duplicate check over 200 open issues | $0.0012 |
| Code context (800-file map, 4 files read) — only for created issues | $0.0026 |
| **Total** | **≈ $0.004** |

1000 reports a month come to about **$3.70** on flex, about $5.50 if every
chat call fell back to the regular tier; keeping the repository map current
adds cents (a first build of a 1000-file repository is about $0.15, after
that only changed files are paid for). The first live dry runs came in well
under the estimate for the draft itself: $0.00006–0.00008 per draft on flex,
plus about 1,000 Jev input tokens for type and title (dedup and code context
add more once a repository with open issues and a map is involved).

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
    "ChatRetryProviders": ["openai"],
    "ChatDeadlineSeconds": 30,
    "ReasoningEffort": "low",
    "DecisionModel": "typesafe/jev-1.13"
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

`ChatProviders`, `ChatRetryProviders` and `IgnoredLabels` fall back to their
defaults when absent; a configured list replaces the default rather than
extending it. `Database:Path` is relative to the working directory
(`db/app.db` by default); the folder is created at startup if it does not
exist. The database only holds caches and hour-long drafts: its schema is
stamped, and a file written by a build with a different schema is rebuilt at
startup rather than failing.

`Apps` is a list, not a dictionary — `owner/repo` contains a `/`, which can't
appear in an env-var name, so a list with a unique `Repo` field stays
overridable. Startup fails fast — every problem printed as a
`CONFIG ERROR: <key> …` line on stderr, exit code 1, before anything
connects — if: the Discord token, OpenRouter key, chat model, decision model or
database path is missing; the decision model is a `~` alias; the chat
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
   is available and — with several apps configured — offers every app in the
   dropdown. The created issue's footer reads "via Discord".
8. **Rate limit.** With `Limits__ReportsPerUserPerDay=1`, a second `/issue`
   within a day answers with the time you can report again instead of a modal.
9. **`/issues`.** The ephemeral list shows the repository's open issues with
   working links.
10. **Image-upload smoke test and GitHub App.** As before: `--smoke-upload
    owner/repo` must print `SMOKE OK: <url>`; then swap the PAT for a
    `GitHubApp` block, restart, run the smoke test again (first line must say
    `auth: GitHub App (installation token)`), repeat step 3, and confirm the
    issue is authored by `<app-name>[bot]` and the code context still
    appears (the App needs Contents: Read).
