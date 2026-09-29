# CLAUDE.md

.NET 10 Discord bot that turns Discord reports into deduplicated GitHub issues.
Read APP.md for what the app does,
docs/superpowers/specs/2026-08-18-discord-github-issue-bot-design.md for the
original design and docs/superpowers/specs/2026-09-29-openrouter-jev-redesign.md
for the OpenRouter/Jev redesign that supersedes its AI, dedup and command parts.

## Working rules
- Commit after each feature; write good conventional-commit messages.
- Write unit tests (XUnit) after finishing a feature — logic lives in testable
  services, the Discord layer stays thin.
- Run `dotnet build` and `dotnet test` before every commit; both must be clean.
- When information is unclear or missing, ask the user (AskUserQuestion tool)
  instead of guessing.
- Keep CLAUDE.md, APP.md, and docs/ up to date with every change.
- Record every non-obvious decision in docs/DECISIONS.md (date + one paragraph).

## Commands
- Build: `dotnet build`
- Test: `dotnet test`
- Run: `./run.ps1` (loads the gitignored `.env`, which the app itself never
  reads) or `dotnet run --project src/DiscordGithubBot` with settings in the environment
- Dry run (one report through every model call, nothing stored or posted):
  `dotnet run --project src/DiscordGithubBot -- --dry-run owner/repo "report text"`
- Image-upload smoke test: `dotnet run --project src/DiscordGithubBot -- --smoke-upload owner/repo`
  (`owner/repo` must be a configured app; prints the app's auth mode, then
  `SMOKE OK: <url>` or `SMOKE FAILED: …`)
- Manual (live-bot) checklist: APP.md → "Manual verification".
- CI: PRs and `main` run build+test; pushes to `main` also release the Docker
  image to ghcr.io (APP.md → "CI/CD", decisions 69-70).

## Gotchas
- Every model call goes through OpenRouter (`OpenRouterChatClient`,
  `DecisionClient`) — never add another AI SDK or endpoint.
- The decision model must be a pinned id (`typesafe/jev-1.13`), never a
  `~...-latest` alias — startup rejects aliases; thresholds belong to a build.
- Flex is only reachable by naming `openai/flex` in `provider.order`
  (a bare `openai` never matches it). Interactive chat calls have a deadline
  (`ChatDeadlineSeconds`) and one retry on `RegularProviders`. A prompt marked
  `ChatTier.Regular` (today: only the draft) skips flex entirely.
- Structured-output schemas are generated from the answer DTO
  (`StructuredOutput`); strict mode needs every property required and no
  nullable members — keep DTO fields non-nullable.
- Decision thresholds (dedup 0.5/0.2, shortlist floor 0.05) are unprobed
  pre-probe defaults; tune them from `--dry-run` output, not by guessing.
- The DB schema is stamped with `PRAGMA user_version`: bump
  `DatabaseSchema.Version` on ANY entity/column/index change, or existing
  databases keep the old schema and the first query fails. For an additive
  change also add the step to `DatabaseSchema.Upgrades` — otherwise the bump
  rebuilds the file and throws away the (paid-for) repository map.
- Repository-map vectors are stamped with `OpenRouter:EmbeddingModel`; only
  vectors from the configured model are searched, and each check re-embeds
  the rest. Code context never shows Jev the whole map — it ranks a
  shortlist from keyword + embedding search (decision 95).
- Discord attachment URLs expire ~24h — bytes are downloaded during the modal
  handler and persisted in SQLite (PendingAttachment).
- Never hotlink Discord CDN URLs in GitHub issue bodies.
- Downloaded attachment bytes are magic-byte sniffed (`ImageSniffer`, PNG/JPEG/
  GIF/WebP only) and the payload carries the *sniffed* content type, never
  Discord's declared one — declared types come from the uploading client and are
  spoofable. SVG is deliberately excluded (scriptable XML, no magic number).
- Everything after `IssueBodyComposer.MetaMarker` in an issue body is bot
  boilerplate (footer, screenshots, code context) and is cut before dedup
  reads it.
- Code context is written to GitHub only — never render it in Discord (any
  user can install the bot; private code must not leak). It is built in the
  background by `CodeContextPrefetcher` after the preview is shown and stored
  on the pending report; the click only picks it up.
- Commands are global and user-installable: never rely on `Context.Guild`;
  ask `AppAccess.ForAsync(Context.Interaction.GuildId, Context.User.Id)`. A
  configured server gets its own apps; anywhere else the user gets the apps
  of the configured servers they are in, checked over REST
  (`IGuildMembership`) — the bot has only the `Guilds` intent, so its member
  cache is never trusted for this.
- All interaction replies are ephemeral; only issue creations post publicly.
- The report modal's "App" dropdown is not declared on ReportModal — its
  options are per-guild, so OpenModalAsync injects it via `modifyModal`, and
  the submit handler reads the pick from the raw modal data when the custom
  id's repo segment is the `-` placeholder (decision 73).
- Components V2 messages carry all their text inside the payload — never pass
  `text:` together with `components:`, Discord rejects the combination.
- Interaction handlers are `RunMode.Sync` on purpose: BotService owns the
  per-interaction DI scope and detaches the dispatch onto its own task itself.
- Each app authenticates with EITHER `GitHubToken` OR a `GitHubApp` block,
  never both — startup fails otherwise. GitHub calls never read the token
  field directly; they ask `IGitHubAuthProvider`, which must stay a singleton
  or its installation-token cache is worthless. The App private key is parsed
  at startup, so an inline PEM in an env var (literal `\n`, not newlines)
  fails validation — use `PrivateKeyPath` outside key-per-file secrets.
- Library log categories (HTTP client, EF) are quieted in appsettings.json,
  not code, so env vars can raise them again. Log what the bot does at
  Information in its own words; per-call detail belongs at Debug.
- The Docker image sets `Database__Path=/data/app.db`; a `.env` (or any env var)
  with a relative path overrides it and puts the db under root-owned `/app`,
  which crash-loops the container. `.env.example` keeps that key commented out.
