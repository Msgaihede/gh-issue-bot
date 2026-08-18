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
   hours. Everything from here on stays private to the reporter until an
   issue is actually created.
3. **Normalize.** The raw text goes to the chat model (`gpt-5.6-luna`), which
   produces a structured `{title, body}` draft from a bug or feature
   template and credits the reporting Discord user. It never invents facts
   that weren't in the report.
4. **Embed.** The normalized text is embedded with `text-embedding-3-small`
   (1536 dimensions) for similarity search.
5. **Cosine top-5.** The bot keeps an incrementally synced, embedded copy of
   the target repo's issues (open, plus closed within the last 30 days) and
   ranks them against the new report by cosine similarity, taking the top 5.
6. **LLM verdict.** Those candidates, plus the normalized report, go back to
   the chat model for a structured verdict: a specific duplicate, an
   uncertain set of candidates, or no match. A parse failure degrades safely
   to "uncertain" rather than guessing.
7. **Outcome handling** (all ephemeral): a match on an **open** issue offers
   "add my report as a comment" or "show my draft instead"; a match on an
   issue **closed under 30 days** asks "still happening?" (yes drafts a new
   issue referencing the old one, no just links it and ends the flow);
   **uncertain** shows the candidates plus a "none of these" escape hatch to
   the draft; **no match** goes straight to the draft.
8. **Preview and confirm.** Every path that can create or comment shows a
   draft preview first; nothing reaches GitHub without an explicit confirm.
9. **Create or comment.** On create: screenshots upload to GitHub and get
   embedded in the body, the issue gets a `bug`/`enhancement` label, a public
   announcement posts in the app's channel(s), and the reporter gets an
   ephemeral confirmation. On comment: screenshots upload the same way and
   the comment is added, but nothing posts publicly.

## Slash commands

- **`/report-issue [app]`** — opens the bug-report modal described above.
- **`/request-feature [app]`** — same flow, using the feature-request
  template and the `enhancement` label instead of `bug`.
- **`/issues [app]`** — an ephemeral list of the target repo's open issue
  titles with links, capped at 25 with a "+K more on GitHub" note.

The `app` option is only needed when the guild maps to more than one
configured app; with a single app it is ignored, and naming an unknown one
answers with the valid names.

## Configuration

Configuration layers in order, last one wins: `appsettings.json` →
`appsettings.{Environment}.json` → environment variables (`__` as the
nesting delimiter) → Docker secrets at `/run/secrets` (via
`Microsoft.Extensions.Configuration.KeyPerFile`, so they always win). Mix and
match freely — e.g. commit non-secret defaults to JSON and supply the
Discord token and API keys as env vars or Docker secrets.

The JSON shape (see `src/DiscordGithubBot/appsettings.json` for the
checked-in defaults, and `.env.example` for the env-var form of every knob):

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

`Apps` is a list, not a dictionary — `owner/repo` contains a `/`, which can't
appear in an env-var name, so a list with a unique `Repo` field stays
overridable. Startup fails fast, naming the offending key, if: there are no
apps; any app has an empty/duplicate repo, empty token, no guild ids, or no
channel ids; or the Discord token / OpenAI key is missing.

## Running locally

`dotnet run --project src/DiscordGithubBot` runs it directly. Copy
`.env.example` to `.env`, fill in real values, and export them into your
shell (or use a tool that loads `.env` files) before running —
`appsettings.json` ships with safe, secret-free defaults, so local runs just
need the Discord token, OpenAI key, and per-app GitHub tokens from elsewhere.

`docker compose up` runs it in Docker instead: `docker-compose.yml` wires an
`env_file: .env` and/or Docker secrets for the Discord token, OpenAI key, and
GitHub PATs, and mounts a named volume for the SQLite database directory.
