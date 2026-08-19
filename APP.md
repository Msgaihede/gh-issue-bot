# APP.md

DiscordGithubBot is a .NET 10 console application that lets the users of one
or more apps report bugs and request features directly from Discord, without
ever needing a GitHub account. It turns a short Discord message into a
well-written, deduplicated GitHub issue (or a comment on an existing one),
with AI doing the normalization and duplicate detection and a human always
confirming before anything is written to GitHub. For the full design
rationale see `docs/superpowers/specs/2026-08-18-discord-github-issue-bot-design.md`;
for why specific choices were made, see `docs/DECISIONS.md`.

## What it does

A Discord server admin configures one or more "apps," each pointing at a
GitHub repository and a set of Discord guilds/channels. Members of those
guilds report a bug or request a feature with a slash command; the bot opens
a modal, takes their description and optional screenshots, normalizes it
with an LLM, checks it against existing GitHub issues, and — after the
reporter confirms — creates a new issue or comments on a matching one. New
issues are also announced publicly in the app's configured channel(s).

## The report workflow

1. **Modal.** The reporter runs `/report-issue` or `/request-feature`,
   naming the app with the optional `app` option when the guild maps to more
   than one. A modal opens with a multiline description field and an
   optional file upload for up to 10 screenshots.
2. **Defer, then immediate download.** On submit, the bot acknowledges the
   interaction ephemerally (Discord allows three seconds) and downloads any
   attached image bytes right away — Discord's CDN URLs expire in about 24
   hours. Anything that is not an image, is larger than 10 MB, or fails to
   download is skipped and listed by name in a warning line above the
   reply, rather than costing the reporter their report. Everything from
   here on stays private to the reporter until an issue is actually created.
3. **Normalize.** The raw text goes to the chat model (`gpt-5.6-luna`), which
   produces a structured `{title, body}` draft following a bug template
   (Description / Steps to Reproduce / Expected / Actual) or a feature
   template (Summary / Motivation / Proposed Solution), translating into
   English if needed. It never invents facts that weren't in the report — a
   section with nothing to say is left out. The reporter credit is not part
   of the draft; it is added to the body at the moment the issue or comment
   is composed (step 9).
4. **Embed.** The normalized text is embedded with `text-embedding-3-small`
   (1536 dimensions) for similarity search.
5. **Cosine top-5.** The bot keeps an incrementally synced, embedded copy of
   the target repo's issues (open, plus closed within the last 30 days) and
   ranks them against the new report by cosine similarity, taking the top 5.
   Issues the bot filed itself are embedded from the reporter's half of the
   body only: a hidden marker separates it from the generated tail (footer,
   screenshots, notes), which is cut off before hashing, embedding and
   excerpting, so two reports never look alike merely for both having come
   through Discord. Every cached vector is tagged with the model that produced
   it: changing the configured embedding model makes the old vectors
   incomparable, so they drop out of the candidate list and the next sync
   re-embeds them rather than ranking them against the new one.
6. **LLM verdict.** Those candidates, plus the normalized report, go back to
   the chat model for a structured verdict: a specific duplicate, an
   uncertain set of candidates, or no match. A parse failure degrades safely
   to "uncertain" rather than guessing.
7. **Outcome handling** (all ephemeral): a match on an **open** issue offers
   "Same issue — add my report" or "Not it — show my draft"; a match on an
   issue **closed under 30 days** asks whether it is still happening ("Still
   happening" drafts a new issue referencing the old one, "Looks fixed" just
   links it and ends the flow); **uncertain** shows the candidates in a
   select menu plus a "None of these — new issue" escape hatch to the draft;
   **no match** goes straight to the draft.
8. **Preview and confirm.** Nothing reaches GitHub without an explicit click
   from the reporter. Every path that creates an issue shows the drafted
   title and body first, behind a "Create issue" button. The duplicate path
   confirms against the matched issue instead — "Same issue — add my report"
   posts the comment straight away, with the draft one click away behind
   "Not it — show my draft".
9. **Create or comment.** On create: screenshots upload to GitHub and get
   embedded in the body under a `### Screenshots` heading, the body ends with
   a `_Created by **<name>** in Discord server **<server>**._` footer (and a
   `Possible regression of #N.` line when the report came out of the
   closed-issue flow), the issue gets a `bug`/`enhancement` label, a public
   announcement posts in the app's channel(s), and the reporter gets an
   ephemeral confirmation. On comment: screenshots upload the same way and
   the comment is added with the same footer, but nothing posts publicly. A
   screenshot that cannot be uploaded is named in a note in the body rather
   than blocking the issue.

Between the modal submit and that final click the draft (with the downloaded
screenshot bytes) lives in SQLite for **one hour**; an hourly background pass
sweeps expired rows, and a click on an older message answers "that report is
no longer waiting". Clicking a button also strips the buttons off the message
it was clicked on, and the confirming click claims the draft in the database
before it touches GitHub, so the same report cannot be filed twice even if two
clicks land at the same instant. A confirmation that fails — GitHub down, say —
gives the claim back, so the draft and its buttons still work for a retry.

## Slash commands

- **`/report-issue [app]`** — opens the bug-report modal described above.
- **`/request-feature [app]`** — same flow, using the feature-request
  template and the `enhancement` label instead of `bug`.
- **`/issues [app]`** — an ephemeral list of the target repo's open issue
  titles with links, capped at 25 (and at what fits Discord's message size,
  whole lines only) with a "+K more on GitHub" note for the rest.

The `app` option is only needed when the guild maps to more than one
configured app; with a single app it can be left out, and naming an unknown
one answers with the valid names. A name that is given is always honoured —
it must match a configured app of that guild, even when the guild has only
one.

## Configuration

Configuration layers in order, last one wins: `appsettings.json` →
`appsettings.{Environment}.json` → environment variables (`__` as the
nesting delimiter) → command-line arguments → Docker secrets at
`/run/secrets` (via `Microsoft.Extensions.Configuration.KeyPerFile`, added
last and only when that directory exists, so where they exist they always
win). Mix and match freely — e.g. commit non-secret defaults to JSON and
supply the Discord token and API keys as env vars or Docker secrets.

The JSON shape (see `src/DiscordGithubBot/appsettings.json` for the
checked-in defaults, and `.env.example` for the env-var form of every knob):

```json
{
  "Discord": { "Token": "<secret>" },
  "OpenAI": {
    "ApiKey": "<secret>",
    "ChatModel": "gpt-5.6-luna",
    "EmbeddingModel": "text-embedding-3-small",
    "ServiceTier": "flex"
  },
  "Database": { "Path": "db/app.db" },
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

`Database:Path` is relative to the working directory (`db/app.db` by
default); the folder is created at startup if it does not exist.

`OpenAI:ServiceTier` picks the processing tier for the two chat calls
(normalization and the duplicate judge; embeddings have no tier). The
default, `flex`, runs them at half price on OpenAI's spare capacity: a
response may queue for a while — the client waits up to five minutes — and
under load OpenAI rejects the call outright instead. Both outcomes are the
pipeline's ordinary failure modes (the normalizer retries once and then
reports failure; the judge degrades to "is this a duplicate?"), so flex
costs latency in the worst case, never correctness. Set `default` to buy
back standard latency at full price; `auto`, `scale`, and `priority` are
accepted too (case-insensitive).

`Apps` is a list, not a dictionary — `owner/repo` contains a `/`, which can't
appear in an env-var name, so a list with a unique `Repo` field stays
overridable. Startup fails fast — every problem printed as a
`CONFIG ERROR: <key> …` line on stderr, exit code 1, before anything
connects — if: the Discord token, OpenAI key, chat model, embedding model or
database path is missing; the OpenAI service tier is not one of the five
known values; there are no apps; or any app has an empty name, a
`Repo` that isn't `owner/repo`, a repo that another app already claims (case
insensitive), no guild ids, no channel ids, or credentials that are not
exactly one of the two forms described next.

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
   (creating issues and comments) and **Contents: Read and write** (the
   tier-2 screenshot fallback commits to the `issue-assets` branch). Nothing
   else is needed.
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

`dotnet run --project src/DiscordGithubBot` runs it directly. Copy
`.env.example` to `.env`, fill in real values, and export them into your
shell (or use a tool that loads `.env` files) before running — the app
itself does not read `.env` files; only compose does. `appsettings.json`
ships with safe, secret-free defaults, so local runs just need the Discord
token, OpenAI key, and per-app GitHub credentials from elsewhere. For an app
on GitHub App credentials that means `Apps__0__GitHubApp__PrivateKeyPath`
pointing at the `.pem` on disk — an exported environment variable cannot
carry the PEM's newlines, and startup rejects the mangled key.

`dotnet build` builds everything, `dotnet test` runs the suite.

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
  `secrets/Discord__Token`, `secrets/OpenAI__ApiKey`, and, if you want the
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

The suite covers the logic; these seven steps cover the parts only a live bot
can prove. Run the bot with a real Discord token, a real OpenAI key and real
GitHub credentials for a test repository, in a guild where that repository is
the guild's **only** configured app. Steps 1–6 are written for a PAT; step 7
repeats what is worth repeating under a GitHub App.

1. **Modal.** Run `/report-issue` in the guild. The `app` option can be left
   empty (one app is configured), and the modal opens immediately — no app
   picker, no extra step. Fill in the description, attach two screenshots,
   submit.
2. **New issue path.** The reply is the AI draft preview with **Create
   issue** / **Cancel**. Press *Create issue* and check that: the issue
   exists on GitHub with both screenshots rendering inline in the body, a
   `bug` label, and a `_Created by **<you>** in Discord server
   **<this server>**._` footer; a public announcement naming the app, the
   issue and the reporter appears in the app's configured channel(s),
   showing both screenshots as a media gallery under the text (on a
   **private** repository the images are behind authentication, so expect
   the gallery to come up blank there); and everything you saw in the
   command channel was ephemeral ("Only you can see this") — the invoker's
   messages never post publicly.
3. **Duplicate path.** Report the same bug again with different wording. The
   reply should be "This looks like an existing issue: #N …" with **Same
   issue — add my report** / **Not it — show my draft**. Press *Same issue —
   add my report* and confirm the comment (with its own screenshots and
   attribution footer) lands on issue #N, and that nothing is announced
   publicly.
4. **Closed-issue path.** Close issue #N on GitHub, then report the same bug
   a third time. The reply should be "This looks like #N …, closed recently.
   Is it still happening?" with **Still happening** / **Looks fixed**. Press
   *Still happening*, then *Create issue*, and confirm the new issue's body
   carries `Possible regression of #N.` (Pressing *Looks fixed* instead just
   links #N and ends the flow without writing to GitHub.)
5. **`/issues`.** Run `/issues` and confirm the ephemeral list shows the
   repository's open issues — it is read live from GitHub, so the issues you
   just filed are in it — with links that open the right issues (and a
   "+K more on GitHub" line if the repo has more than fits).
6. **Image-upload smoke test.** Run
   `dotnet run --project src/DiscordGithubBot -- --smoke-upload owner/repo`
   for the same repository and confirm it prints `SMOKE OK: <url>` and that
   the URL opens the 1×1 PNG. A `github.com/user-attachments/…` URL means
   the unofficial endpoint worked with your PAT; a
   `raw.githubusercontent.com/…/issue-assets/…` URL is still a pass — it
   records that the PAT cannot use the unofficial endpoint and that the
   Contents-API fallback engaged (the warning in the log names the reason).
7. **GitHub App credentials.** Swap the app's `GitHubToken` for a
   `GitHubApp` block (see "GitHub credentials" above) and restart. Run the
   smoke test again: the first line must now read
   `auth: GitHub App (installation token)`, and whichever URL it prints is
   the answer to whether the unofficial endpoint accepts App tokens — record
   it. Then repeat step 2 and confirm the issue is authored by
   `<app-name>[bot]` rather than by you, and that the body, label, footer
   and public announcement are unchanged. Leave the bot running past the
   hour if you can: the second report after that point exercises the token
   refresh, and the log says `Minted a GitHub App installation token …` once
   per hour, not once per report.
