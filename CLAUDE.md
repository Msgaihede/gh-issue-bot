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

## Gotchas
- Chat model must be `gpt-5.6-luna` — bare `gpt-5.6` routes to a 10x-cost tier.
- Embedding dimension (1536) is defined once in VectorRanker.EmbeddingDimensions.
- Discord attachment URLs expire ~24h — bytes are downloaded during the modal
  handler and persisted in SQLite (PendingAttachment).
- Never hotlink Discord CDN URLs in GitHub issue bodies.
- float[] embeddings map to BLOB via a ValueConverter + ValueComparer (both required).
- All interaction replies are ephemeral; only issue creations post publicly.
- Components V2 messages carry all their text inside the payload — never pass
  `text:` together with `components:`, Discord rejects the combination.
- Interaction handlers are `RunMode.Sync` on purpose: BotService owns the
  per-interaction DI scope and detaches the dispatch onto its own task itself.
