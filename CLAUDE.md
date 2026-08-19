# CLAUDE.md

.NET 10 Discord bot that turns Discord reports into deduplicated GitHub issues.
Read APP.md for what the app does and
docs/superpowers/specs/2026-08-18-discord-github-issue-bot-design.md for the design.

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
- Run: `dotnet run --project src/DiscordGithubBot`
- Image-upload smoke test: `dotnet run --project src/DiscordGithubBot -- --smoke-upload owner/repo`
  (`owner/repo` must be a configured app; prints the app's auth mode, then
  `SMOKE OK: <url>` or `SMOKE FAILED: …`)
- Manual (live-bot) checklist: APP.md → "Manual verification".
- CI: PRs and `main` run build+test; pushes to `main` also release the Docker
  image to ghcr.io (APP.md → "CI/CD", decisions 69-70).

## Gotchas
- Chat model must be `gpt-5.6-luna` — bare `gpt-5.6` routes to a 10x-cost tier.
- Embedding dimension (1536) is defined once in VectorRanker.EmbeddingDimensions.
- Cached vectors are stamped with `IssueEmbedding.EmbeddingModel`; rows from
  another model are never ranked — sync re-embeds them (from the stored title +
  body excerpt) so a model switch heals without a full resync.
- Discord attachment URLs expire ~24h — bytes are downloaded during the modal
  handler and persisted in SQLite (PendingAttachment).
- Never hotlink Discord CDN URLs in GitHub issue bodies.
- float[] embeddings map to BLOB via a ValueConverter + ValueComparer (both required).
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
- The Docker image sets `Database__Path=/data/app.db`; a `.env` (or any env var)
  with a relative path overrides it and puts the db under root-owned `/app`,
  which crash-loops the container. `.env.example` keeps that key commented out.
