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
   Contents API on an `issue-assets` branch if that fails (the branch is not
   orphaned — the REST API cannot create one; see decision 13). Discord CDN
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
    disguised as a normalization failure. *(Revised 2026-08-18 — see decision
    40: that rethrow is now guarded on `ct.IsCancellationRequested`, because a
    client-side timeout arrives as a `TaskCanceledException` with nothing
    cancelled and is an ordinary failed attempt, not a cancellation.)* Both services read model output with
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

18. **Body composer emits `\n` line endings and passes image names/URLs
    through verbatim.** `IssueBodyComposer` hard-codes `\n` rather than
    `Environment.NewLine` so the same report produces byte-identical markdown
    on Windows and Linux (GitHub normalizes either form, so nothing is lost),
    which keeps the output snapshot-testable. Section blocks are joined by a
    single blank line through one private `AppendBlock` helper, and the draft
    is trimmed and skipped when empty so a body never opens with blank lines.
    Image `Url` is interpolated into `![name](url)` without markdown escaping:
    it comes from our own upload step (Task 6), which returns a GitHub-hosted
    URL, and escaping it would corrupt legitimate links for no gain. *(Revised
    2026-08-18 — see decision 43: this entry originally claimed the same about
    `FileName`, which was wrong. The upload step controls the URL, not the
    name: `FileName` is whatever the reporter called their screenshot in
    Discord, and it is now escaped.)*

## 2026-08-18 (issue sync)

19. **Repo keys are lowercased inside the sync service, on both the write and
    the read side.** `IssueEmbedding.RepoKey` is documented as lowercase, but
    callers hold an `AppConfig.Repo` written by hand in configuration and the
    report pipeline passes that value straight to `GetCandidatesAsync`.
    Normalizing in one place — `IssueEmbedding.RepoKey`, `RepoSyncState.RepoKey`
    and the candidate lookup all go through the same helper — keeps a
    `Owner/Repo` config entry from producing a cache that can never be read
    back, without asking every caller to remember the convention. SQLite
    compares TEXT case-sensitively by default, so this is what makes the
    lookups reliable rather than a cosmetic detail.

20. **The 30-day prune is a single repo-agnostic `ExecuteDeleteAsync`, and
    candidates are read untracked.** The retention rule ("a closed issue stops
    being a dedup candidate 30 days after it closed") is uniform across
    repositories, so scoping the delete to the requested repo would only leave
    other repos' rows to rot until someone happens to query them; the delete
    also runs as one SQL statement rather than loading rows, which matters
    because every row drags a 6 KB vector BLOB. `AsNoTracking()` on the
    candidate query is the same economy: candidates are read-only inputs to
    ranking and the judge, and tracking them would make the change tracker
    snapshot-clone every vector.

21. **Only a cancellation of our own token is rethrown, and the content hash
    advances only after the vector it describes is in hand.** `SyncAsync`
    swallows GitHub and embedding failures per the resilience decision, but a
    genuine cancellation is rethrown ahead of that catch, matching the AI
    services (decision 15): a cancelled sync is a shutting-down host, not a
    stale cache worth logging as a warning. The rethrow is guarded with
    `when (ct.IsCancellationRequested)` because `HttpClient` reports its own
    timeout as a `TaskCanceledException` with nothing cancelled — an unguarded
    `catch (OperationCanceledException) { throw; }` would let the single most
    likely GitHub failure escape a method whose contract is that it never
    throws on GitHub failure, and the report pipeline awaits it bare on that
    promise. A new row is inserted with an empty `ContentHash` and both new
    and changed rows get their hash set only inside the embedding step, so an
    embedding that throws mid-batch leaves the row looking stale — the next
    sync retries it instead of treating a vector-less row as up to date.

22. **A failed sync rolls its own unsaved edits out of the change tracker.**
    The `BotDbContext` is shared with the rest of the operation, so swallowing
    an embedding failure while leaving half-written `IssueEmbedding` rows
    tracked would hand the mess to the caller: their next `SaveChanges` would
    flush the incomplete rows, and a retry of the sync would add a *second*
    tracked insert for the same issue and break the unique
    (`RepoKey`,`IssueNumber`) index — one failed sync poisoning the whole
    context, the exact opposite of the swallow-and-continue contract. The
    catch therefore detaches Added and reverts Modified entries for the two
    entity types this service owns, leaving the cache byte-for-byte as it was
    before the sync started — on the cancellation path too, which rolls back
    before rethrowing, since a cancelled host may still reuse the context to
    finish the in-flight interaction. *(Revised 2026-08-18 — see decision 41:
    the rollback now covers the failed tail only. Batches the sync already
    flushed are `Unchanged` and stay, which is the point of the batching; the
    watermark still does not move, so they are re-checked on the next pass.)* Verified with a flaky embedder plus an
    unrelated `PendingReport` save on the same context, and pinned in the
    committed suite by `Cancellation_propagates_and_leaves_no_half_written_rows_tracked`.

## 2026-08-18 (report pipeline)

23. **Pending reports expire on read as well as on the maintenance pass, and
    the read that finds an expired row deletes it.** `PendingReportStore.GetAsync`
    treats anything older than an hour as gone even though `CleanupExpiredAsync`
    will remove it eventually: a report is only ever read because a reporter
    clicked a button on an old ephemeral message, and the hourly sweep may not
    have run since. Enforcing the lifetime at the only place it matters means a
    stale draft can never be resurrected by a late click, and reaping the row on
    the way out keeps a repeatedly-clicked dead message from accumulating.

24. **Deleting a pending report is one SQL statement and relies on the schema's
    cascade for the attachment rows.** `ExecuteDeleteAsync` never loads the
    entities, which matters because every `PendingAttachment` carries a
    screenshot's bytes — loading a report just to throw it away would pull
    megabytes through the change tracker. The dependent rows go with the parent
    via the `ON DELETE CASCADE` that EF writes into the SQLite schema
    (Microsoft.Data.Sqlite turns the foreign-keys pragma on by default). That is
    a real dependency rather than an assumption, so it is pinned by the test
    `Deleting_a_report_takes_its_attachment_blobs_with_it`, which fails loudly if
    the pragma or the cascade ever stops holding instead of quietly leaking blobs.
    Candidates are read with `AsNoTracking().Include(...)` for the same economy:
    callers only ever read the draft and its bytes.

25. **The pending report stores the whole ranked shortlist, not just the
    candidates the verdict routed on.** `CandidatesJson` is written from all
    (up to five) ranked issues even when the verdict is Match or NoMatch, because
    the reporter's next click can change the flow — "none of these" after an
    Uncertain, or "this is not a duplicate" — and the alternative is a second
    embedding + ranking pass to rebuild a list we already had. It costs a few
    hundred bytes per pending row, all of which is deleted within the hour.

26. **A verdict the judge should not be able to produce degrades instead of
    throwing.** `DuplicateJudge` already guarantees that a Match names an issue
    it was offered and that Uncertain lists a subset of those numbers, so both
    guards in `ReportPipeline.Route` are for contract violations, not expected
    paths: a Match on an unknown number falls back to Uncertain over the whole
    shortlist (ask the reporter rather than link an issue we cannot show), and an
    Uncertain whose numbers match nothing falls back to NoMatch (an "is it one of
    these?" prompt with nothing to choose from is a dead end for the reporter).
    Both are logged as warnings and both are covered by tests, so the fallbacks
    stay honest rather than becoming dead code.

27. **The pending report is deleted only after GitHub accepts the issue or
    comment.** `CreateIssueAsync` and `AddCommentAsync` call `store.DeleteAsync`
    after the GitHub call returns, so a failed call leaves the draft, its
    attachments and its one-hour window intact and the reporter can press the
    button again. The screenshots are re-uploaded on that retry, which can leave
    an orphaned asset behind — a far cheaper failure than losing the report.
    Uploads run sequentially rather than in parallel: it is a handful of images
    against one repository, and the gallery then keeps the order the reporter
    attached them in.

28. **The app is chosen with a slash-command option, not a select menu inside
    the modal.** *(Superseded by decision 73: the report commands now ask via
    a dropdown in the modal; only `/issues` still takes the option.)*
    The design spec sketched an app selector as the modal's first
    row. It is implemented as an optional `app` option on `/report-issue`,
    `/request-feature` and `/issues` instead: the chosen repository then rides
    along in the modal's custom id, which leaves the modal as a single
    description field plus the file upload, and leaves the submit handler with
    no state to look up. Guilds with one configured app never see the option
    at all, and naming an unknown app answers with the valid names.
    `AppResolution` is a plain static class rather than a private method so the
    three rules (no app / named app / several apps) can be unit-tested.

29. **Components V2 messages carry all of their text inside the payload.**
    Discord rejects a message that sets both the CV2 flag and message content,
    so nothing in the Discord layer ever passes `text:` together with
    `components:`. The skipped-attachment warning and the flow headings became
    optional `notice`/`heading` arguments on the renderers rather than a
    leading line of message content — a deviation from the task brief's sketch,
    which passed the notice as `text`. Discord.Net 3.20.1 sets
    `MessageFlags.ComponentsV2` by itself on `RespondAsync`, `FollowupAsync`
    and `SendMessageAsync` when the payload contains a V2 component, but *not*
    on `IComponentInteraction.UpdateAsync`, which is the one place the flag is
    passed explicitly. Two further names differ from the brief's sketch: the
    fluent `WithContainer`/`WithTextDisplay`/`WithActionRow`/`WithButton`/
    `WithSelectMenu` calls are extension methods in `ComponentContainerExtensions`
    rather than members of the builders, and the optional file upload needs
    `[RequiredInput(false)]` next to `[ModalFileUpload]` for Discord to accept
    a submit with no screenshots.

30. **Every interaction runs on its own task, in its own DI scope, with the
    handler running inline.** `BotService` injects `IServiceScopeFactory` and
    gives each interaction a fresh async scope instead of handing the
    interaction framework the root provider: `IReportPipeline` and the database
    context are scoped services, and resolving them from the root would share
    one change tracker across every reporter. The scope may only be disposed
    once the handler is done, which is why every command carries
    `runMode: RunMode.Sync` — Discord.Net's default `RunMode.Async` detaches
    the handler onto its own task and returns immediately, which would dispose
    the scope out from under a report that is still running. Running inline
    would instead block the gateway's event loop, so `BotService` does the
    detaching itself: it starts the whole dispatch on a `Task.Run` and returns
    to the gateway at once. The next reporter's three-second acknowledgement
    window therefore never queues behind someone else's model calls.

31. **A clicked message loses its buttons before the slow work starts.** Every
    component handler answers the interaction by *updating* the message it was
    clicked on into a "working on it" note with no components, which both meets
    the three-second deadline and makes a second click on that message
    impossible. If Discord refuses the update the handler falls back to a plain
    ephemeral defer and logs it, so the click is still acknowledged. Two clicks
    landing at the same instant can still both get through, and the pipeline
    settles that race with the claim described in decision 49. *(Revised
    2026-08-18 — this entry previously claimed the race was settled by the
    delete-after-GitHub ordering. It was not: the delete happens after the
    GitHub call, so two clicks that both read the report before either finished
    would both create an issue.)*

32. **The Discord layer answers with words, never with a stack trace.**
    `ExpiredPendingReportException` and `NormalizationException` are the two
    typed failures with something to say to a reporter, so they get their own
    ephemeral messages; anything else is logged at error level and answered with
    a generic apology. `BotService` adds a last line of defence for interactions
    the framework could not route at all — a button left over from an older
    version of the bot — so no click is ever left hanging as "interaction
    failed". Announcements are the one exception that stays quiet: a channel
    that no longer exists is a warning in the log, never an error the reporter
    sees, because the issue already exists by then.

## 2026-08-18 (host wiring)

33. **The embedding dimension is passed to the generator, not merely assumed.**
    The host registers
    `AsIEmbeddingGenerator(VectorRanker.EmbeddingDimensions)` rather than
    letting `text-embedding-3-small` supply its own default, because
    `VectorRanker.TopK` silently skips any cached vector whose length differs
    from the query's: a model or default that changed the dimension would not
    fail loudly, it would quietly return zero duplicate candidates and turn
    every report into a new issue. Passing the one constant is what makes
    decision 7's "fixed in exactly one constant" true at the only point where
    the number leaves the process.

34. **The image uploader is a typed `HttpClient` and stays transient.**
    `AddHttpClient<IImageUploader, GitHubImageUploader>` registers the
    implementation transiently, so `GitHubImageUploader`'s repository-id cache
    is not shared process-wide. That is deliberate rather than accepted: the
    only consumer, `ReportPipeline`, is scoped and injects the uploader once,
    so a single instance serves every screenshot in a report and the
    repository id is fetched once per report instead of once per image —
    which is where that cache earns its keep. Promoting the uploader to a
    singleton to widen the cache would pin one pooled `HttpMessageHandler` for
    the life of the process and defeat `IHttpClientFactory`'s handler
    rotation, a real cost for an optimisation worth one GET per report.

35. **`InteractionService` is built from `BotService.CreateConfig()`.** The
    host composes no `InteractionServiceConfig` of its own. The Discord
    layer's contract (decision 30) is that handlers run inline under
    `RunMode.Sync` so `BotService` can own each interaction's DI scope, and
    `CreateConfig()` is the single place that setting lives; a hand-rolled
    `new InteractionServiceConfig { UseCompiledLambda = true }` at the
    registration would silently restore Discord.Net's default
    `RunMode.Async` and dispose every scope out from under a running report.

## 2026-08-18 (docker)

36. **`.dockerignore` excludes build output by glob, not by top-level name.**
    `bin/` and `obj/` in a `.dockerignore` only match at the context root,
    which would let the *host's* `src/DiscordGithubBot/obj/` into the build
    context — and a `project.assets.json` generated on Windows carries
    Windows package paths that break the image's `dotnet publish --no-restore`.
    The file therefore uses `**/bin/` and `**/obj/`. `.superpowers/` is
    excluded for the same reason the other tooling directories are: nothing
    outside `src/` and the two project files is needed to build the bot, and
    every excluded path is one less cache-busting change to the context.

37. **Compose secret names are the config keys, and the blocks are optional.**
    Each entry under `secrets:` is named exactly as the KeyPerFile provider
    will read it (`Discord__Token`, `OpenAI__ApiKey`), because the file name
    *is* the configuration key — renaming a secret silently stops overriding
    anything. Only the two always-needed secrets ship in the file; per-app
    GitHub PATs (`Apps__0__GitHubToken`) are equally valid as secret files
    but are left to `.env` by default, since their count varies per install.
    Compose refuses to start when a referenced secret file is missing, so
    APP.md tells `.env`-only users to delete both `secrets:` blocks rather
    than create empty placeholder files.

## 2026-08-18 (documentation reconciliation)

38. **`MaintenanceService.ExecuteAsync` swallows the guarded cancellation
    instead of propagating it.** The plan's snippet let the shutdown
    `OperationCanceledException` escape, which would leave the background
    service's task in the `Canceled` state; the implemented loop wraps the
    `PeriodicTimer` loop in
    `catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)`
    and returns, so the task completes `RanToCompletion` on shutdown. This was
    reviewed and accepted rather than "fixed": under the Generic Host the two
    are runtime-equivalent — `StopAsync` awaits the task and treats a
    cancellation of its own stopping token as a clean stop either way, nothing
    is logged differently, and no exit code changes. The guard is the point of
    the shape and matches decision 21: only a cancellation of *our* token is
    treated as shutdown, so a stray `TaskCanceledException` from inside a
    cleanup pass still reaches the inner warning-level catch rather than
    silently ending the sweep. The one theoretical cost is a future host (or
    diagnostics) that distinguishes a cancelled hosted service from a completed
    one; nothing observable today depends on it.

39. **`.env.example` ships `Database__Path` commented out.** A `.env` copied
    from the example and handed to compose sets *container* environment
    variables, which override the image's `ENV Database__Path=/data/app.db`.
    The previous example carried an active `Database__Path=db/app.db` line, so
    a by-the-book copy pointed SQLite at `/app/db` — a directory the non-root
    `app` user cannot create — and `restart: unless-stopped` turned the
    resulting startup failure into a crash loop, with the `botdata` volume
    silently unused. The key is therefore commented out (with the container
    path as the commented value): local runs fall back to `appsettings.json`'s
    `db/app.db` and Docker runs keep the image's `/data/app.db`, so the example
    is safe in both places. APP.md's Docker section states the same rule
    explicitly instead of claiming that everything in `.env.example` works in a
    container unchanged.

## 2026-08-18 (final review fixes)

40. **Both AI services guard their cancellation catch on our own token.**
    `ReportNormalizer` and `DuplicateJudge` caught `OperationCanceledException`
    and rethrew it unconditionally, which was wrong for the same reason
    decision 21 gives for the sync service: `HttpClient` and the OpenAI client
    report *their own* timeout as a `TaskCanceledException` with nobody's token
    cancelled. An unguarded catch therefore turned a routine model timeout into
    an escaping exception that skipped the very fallbacks these two classes
    exist for — the normalizer's retry and the judge's degrade-to-Uncertain —
    and surfaced to the reporter as the generic apology. Both now read
    `catch (OperationCanceledException) when (ct.IsCancellationRequested)`, so a
    timeout falls through to the ordinary `catch (Exception)` and a real
    shutdown still propagates. `TimingOutChatClient` (first call throws
    `TaskCanceledException("timeout", new TimeoutException())`, later calls
    answer normally) pins both halves in each suite.

41. **A cold sync flushes every 25 issues; the watermark still moves only on a
    complete pass.** The first sync of an established repository is hundreds of
    embedding calls long, and the single `SaveChangesAsync` at the end meant a
    rate limit on the last issue threw away every vector already paid for — and
    then did it again on the next attempt. `SyncAsync` now calls
    `SaveChangesAsync` every `DefaultSaveBatchSize` (25) upserts inside the
    loop. `RepoSyncState` is deliberately *not* touched in that loop: the
    watermark is still written once, after the whole window succeeded, so a
    half-finished pass is repeated in full rather than skipped as done. The
    cost of a repeat is nil, because an issue whose content hash already
    matches is not re-embedded. `RollbackPendingChanges` keeps its meaning for
    the tail: rows saved by an earlier batch are `Unchanged` and untouched,
    only the failed tail is detached or reverted.

42. **The batch size is a constructor parameter with a default rather than a
    private const.** `IssueSyncService(..., int saveBatchSize =
    DefaultSaveBatchSize)` is the one test-only seam in the class. The
    alternative — a private const and a 30-issue fixture to cross the
    boundary — makes the test about arithmetic instead of about the behaviour,
    and it would silently stop covering anything if the constant changed. The
    default keeps production and DI untouched: `Microsoft.Extensions.
    DependencyInjection` fills parameters it cannot resolve from their default
    values, which `HostSetupTests` proves by resolving `IIssueSyncService` from
    a real container.

43. **The body composer escapes every string it did not author.** Decision 18
    justified passing image names through verbatim on the grounds that "our own
    upload step controls the names". That was false: `UploadedImage.FileName` is
    the reporter's Discord attachment name, echoed straight back by the
    uploader, and `failedUploads` carries the same names for the screenshots
    that never made it. `ReporterDisplayName` is a Discord display name and is
    just as attacker-chosen. A screenshot called
    `x](http://evil)![` therefore rewrote the markdown image link built around
    it — the same class of bug `OutcomeRenderer.Inline` already guarded against
    on the Discord side. A private `Escape` helper now backslash-escapes the
    backslash itself, `[`, `]`, `(`, `)`, the backtick and `<`, and folds CR/LF
    to a space, in image names, failed-upload names and the reporter name. The
    backslash belongs in that set and its omission was a real hole for one
    review round: escaping runs character by character, so an attacker's own
    backslash was emitted raw immediately before the one `Escape` prepends, the
    pair rendered as a single literal backslash, and the character behind it was
    armed again — `\<a href="https://evil.example">click me\</a>` in a display
    name came out as live HTML, which GitHub renders. Escaping the backslash
    first closes it, because every later escape is then written behind an
    already-neutralized one. `>` is deliberately *not* escaped: with newlines
    gone, a `>` cannot open a blockquote, and escaping it made ordinary names
    like `<v2>` uglier for nothing. The draft body is *not* escaped — it is the
    model's own markdown and exists to render — and neither is the image URL,
    for the reason decision 18 gives. Escaping is applied at interpolation time
    rather than at storage time so the stored draft stays the reporter's text.

44. **The upload content type is parsed, not constructed.**
    `new MediaTypeHeaderValue(contentType)` throws on anything but a bare media
    type, and Discord attachments regularly carry parameters
    (`image/png; charset=utf-8`). The throw happened inside the tier-1 `try`,
    so it did not lose the screenshot — it silently sent every such upload down
    the Contents API fallback, permanently darkening the tier the whole design
    prefers and leaving a commit on `issue-assets` for each one.
    `MediaTypeHeaderValue.Parse` accepts the parameterized form, and a genuinely
    malformed value still throws into the same fallback as before.

45. **Every rendered Components V2 message is budgeted, not just the draft
    body.** Discord rejects a CV2 message whose text runs past 4000 characters,
    and a rejected message means the reporter sees nothing — strictly worse
    than a truncated preview. Only the body was capped (3000); the draft title
    and the skipped-attachments notice were interpolated whole, and both are
    reporter- or model-supplied. `OutcomeRenderer` now caps the title at 150
    (the prompt asks for 80, so this is the hard stop, not the target) and the
    notice at 300 — enough for a handful of file names — and every text display
    built by the `Render*` helpers goes through `Budgeted`, a final cut at 3800
    that leaves headroom for the components wrapped around the text. The caps
    compose: each part is cut on its own so no single runaway part can starve
    the others out of the message.

46. **A reply Discord refuses is logged as a delivery failure, not as a failed
    report.** The modal handler wrapped `ProcessAsync` *and* the outcome
    `FollowupAsync` in one `try`, so a payload Discord rejected was logged
    "Report pipeline failed" — pointing whoever read the log at the pipeline
    when the pipeline had in fact succeeded and saved the draft. The two are
    now separate: the pipeline's catches return, and rendering and delivery
    have their own catch that logs "Could not deliver the report outcome" and
    answers with plain text, which is the one shape Discord cannot reject for
    its structure. That fallback is itself wrapped, because if the gateway is
    the problem the second call fails too, and an unhandled throw there would
    only be caught by `BotService` and logged a third way.

47. **Module discovery runs inside a DI scope, even though Discord.Net 3.20.1
    does not currently need it.** `BotService` handed `AddModulesAsync` the root
    provider, reasoning that discovery outlives every interaction. That confuses
    the *metadata* (which does outlive them) with the *instance* Discord.Net
    creates while building a module so it can call `OnModuleBuilding`.
    `ReportInteractionModule` takes the scoped `IReportPipeline`, and
    constructing it from the root provider under `ValidateScopes` throws — which
    `HostSetupTests` now pins directly with `ActivatorUtilities.CreateInstance`.
    Measured honestly: on Discord.Net 3.20.1 the root-provider call *does not*
    throw today, so this is a latent bug rather than a live one; discovery
    evidently does not construct the module on this version. The fix is taken
    anyway because it is free — one scope, disposed the moment discovery
    returns, in a `using` block rather than a method-lifetime `using var` so it
    does not hold a `BotDbContext` open for the process's life — and because
    nothing about the module's shape guarantees the current behaviour survives
    an upgrade or the first `OnModuleBuilding` override. `IServiceProvider
    rootServices` left the constructor with the last use of it.

48. **`HostSetupTests` builds its container with `ValidateOnBuild` as well as
    `ValidateScopes`.** Scope validation catches a singleton capturing a scoped
    service; build validation catches a constructor parameter nothing
    registers, at build time rather than on the first interaction at three in
    the morning. Both are green, so both stay on, and the two module tests
    share one `BuildProvider` helper so no test can quietly opt out of a guard.

49. **A pending report is claimed, not merely read, before a confirmation click
    acts on it.** Decision 31 asserted that the pipeline settled a double click
    by itself. It did not. Both handlers used to `GetAsync` the report, upload
    the screenshots, call GitHub, and only then `DeleteAsync` — so two clicks
    landing inside that window (a double-tap, or the same message clicked from
    two devices) each read a live report and each created an issue. The delete
    ordering that decision 27 requires is exactly what left the window open.
    `PendingReport` therefore gains `ClaimedAtUtc`, and the store gains a
    claim-and-consume pair: `TryClaimAsync` is a single
    `ExecuteUpdateAsync` whose WHERE clause carries the whole precondition
    (`Id = id AND ClaimedAtUtc IS NULL AND` not expired), so SQLite decides the
    race, and only the caller whose statement reports exactly one affected row
    loads the report. `CreateIssueAsync`/`AddCommentAsync` claim instead of
    read; the loser gets `null`, which is the same
    `ExpiredPendingReportException` an expired report already produced.
    `ReleaseClaimAsync` puts the claim back on any failure, which is what keeps
    decision 27 true: a 502 from GitHub still costs the reporter nothing.
    Release runs on `CancellationToken.None` and swallows its own errors, so it
    cannot replace the real exception with a bookkeeping one; a claim it fails
    to return simply expires with the report.

50. **Expired, unknown and already-claimed share one reply.** The three are
    genuinely different states, but a reporter cannot act on the difference —
    in every case the buttons no longer do anything and the answer is to run the
    command again. `ExpiredPendingReportException` therefore covers all three
    and the Discord layer answers with one message: "That report is no longer
    waiting — it expired, or another click is already handling it. Please run
    the command again." Naming the concurrent case explicitly is deliberate: a
    reporter who double-clicked and sees "expired" one second after submitting
    reasonably concludes the bot is broken.

51. **`PeekAsync` deliberately ignores claims.** The non-destructive read behind
    the draft-preview, pick and "looks fixed" handlers still uses `GetAsync`.
    Peeking at a report someone else is in the middle of creating shows the
    reporter their own draft, which is harmless; making peek claim-aware would
    turn a read into a lock and break the handlers that peek and then act.

52. **The `ClaimedAtUtc` column ships without a migration.** Per decision 12 the
    schema is materialized with `EnsureCreated()`, which creates missing tables
    but never alters an existing one. This project is greenfield and unreleased,
    so there is no deployed database to migrate; anyone who has been running a
    build from before this change must delete their SQLite file (it holds only a
    rebuildable embedding cache and drafts younger than an hour) or the first
    query against `PendingReports` will fail on the missing column. The day the
    bot has real users is the day this repo needs EF migrations, and that
    trade-off is unchanged by this entry.

53. **The channel announcement carries a media gallery, and the uploaded images
    ride on `CreatedIssueResult` to get there.** The spec's Discord-layer
    section asks for "media gallery for screenshots in the channel
    announcement"; the implementation announced text only, which was a silent
    drift, not a decision. The obstacle was ownership: by the time the
    announcement renders, the pending report and its attachment bytes have been
    deleted, and the only surviving handle on the screenshots is the list of
    GitHub URLs the upload step returned. `CreatedIssueResult` therefore gains
    `IReadOnlyList<UploadedImage> Images`, and `RenderAnnouncement` adds a
    `MediaGalleryBuilder` below the text when it is non-empty, capped at
    `MediaGalleryBuilder.MaxItems`. Discord.Net 3.20.1's
    `ComponentContainerExtensions.WithMediaGallery(IEnumerable<string>)` takes
    plain URLs, so no fallback to a text list of links was needed. **Caveat
    worth knowing:** a media gallery item is fetched by Discord, not by the
    viewer, so it only renders for URLs Discord can reach anonymously. That is
    true of `user-attachments` assets on public repositories; for a *private*
    repository the tier-2 `raw.githubusercontent.com` URLs require a token and
    the gallery item will fail to load. The text line above the gallery always
    names and links the issue, so the announcement still does its job — but a
    private-repo install should expect blank thumbnails until it is verified
    against a real private repo (the manual checklist is the place for that).

54. **`AppConfig.Repo` trims on assignment.** Every config source that is a file
    or an environment variable can carry stray whitespace — a Docker secret
    written by `echo` ends in a newline, and a hand-edited `.env` picks up
    leading spaces. `" owner/repo"` passed validation (it splits into two
    non-blank halves) and then produced `GET repos/ owner/repo/issues`, a 404 on
    every call with nothing in the logs pointing at the config. Trimming in the
    setter rather than in `Validate` means the value is clean everywhere it is
    read — the repo key, the API paths, the `AppByRepo` lookup — no matter which
    layer supplied it. Only `Repo` is trimmed: it is the one field whose
    whitespace is silently fatal rather than merely untidy.

55. **`AddCommentAsync` fails when GitHub returns no `html_url`.** It used to
    fall back to `""`, which meant the reporter was told "Added your report to
    [#7]()" — a confirmation with a dead link, and a success as far as the
    pipeline was concerned, so the pending report was deleted. A response
    without the field is one we do not understand, so it is now an
    `HttpRequestException`, which reaches the component handler's generic
    apology and (since decision 49) releases the claim so the draft survives.
    The trade-off is the one decision 27 already accepts: if GitHub really did
    post the comment and only the response was malformed, a retry posts it
    twice. A duplicate comment is recoverable; a lost report with a broken link
    is not.

## 2026-08-18 (adjudicated spec drifts)

Two places where the implementation knowingly departs from the design spec.
Both were reviewed against the spec's own constraints and kept; recording them
here so the next reader does not "fix" the code back to the letter of a
document that contradicts itself.

56. **The modal handler defers *before* downloading attachments, reversing the
    spec's step 2.** The spec's report-pipeline step 2 reads "download
    attachment bytes immediately (Discord CDN URLs expire ~24 h), then
    `DeferAsync(ephemeral: true)`". Taken literally that loses the interaction:
    Discord gives an interaction three seconds to be acknowledged, and
    downloading up to ten images over the network is exactly the kind of work
    that blows through it — after which the defer fails and the reporter sees
    "This interaction failed" with their typed-out report gone. The spec's own
    error-handling section states the governing rule, "defer before slow work;
    every failure path ends in an ephemeral message — never a hung
    interaction", so the two clauses conflict and the deadline wins. The
    concern behind the spec's ordering is honoured all the same: the download
    is still the *first* thing after the defer and long before any model call,
    and the ~24 h CDN expiry is nowhere near a three-second acknowledgement.
    `OnReportModal` therefore reads `DeferAsync` → `DownloadAsync` →
    pipeline.

57. **`/issues` shows the first 25 open issues plus a "+K more on GitHub" line,
    not a paginated list.** The spec's Discord-layer section asks for an
    "ephemeral paginated list of open issue titles linking to GitHub".
    Pagination means stateful buttons: a page cursor in every custom id, or a
    server-side cursor with its own TTL and cleanup, plus handlers for an
    interaction that Discord expires after 15 minutes — for a read-only
    convenience view of data that is one click away on GitHub itself, already
    linked from every line. The cap is not arbitrary: 25 is Discord's own limit
    for select options and the number `OutcomeRenderer` already uses for the
    duplicate shortlist, so the two lists agree. Lines are dropped whole rather
    than cut, and everything not shown is counted into a single
    "+K more on GitHub" line, so the reporter is never misled into thinking
    they are seeing everything. If a repository routinely has more than 25 open
    issues *and* the list is being used to triage rather than to check "is this
    known", pagination is the right follow-up — the renderer is a pure function
    and the change would be local to it.

## 2026-08-19 (guild attribution)

58. **The GitHub footer names the Discord server as well as the reporter.** The
    body composer used to end every issue and comment with
    `_Reported by **<name>** via Discord._`; it now reads
    `_Created by **<name>** in Discord server **<server>**._`. The old wording
    was pinned by the plan and by four tests, so those tests were changed
    deliberately, not repaired: the requirement moved by user directive, and a
    test that pins a superseded requirement is the thing that is wrong. One
    footer serves both `ComposeIssueBody` and `ComposeCommentBody` — a comment
    is the same report reaching the same repository by a different route, and
    two attribution formats would only invite drift. A blank server name keeps
    the old `via Discord` tail but not the old verb —
    `_Created by **<name>** via Discord._` — so an interaction that somehow
    carries no guild still credits someone rather than pointing at an empty
    pair of asterisks, and it still reads as the same sentence as the one
    beside it. The server name goes through the same `Escape`
    as the display name (decision 43): a guild owner picks their server name,
    which makes it attacker-chosen text by the same reasoning that covers
    display names and file names. The Discord-side announcement still says
    "Reported by <name> via Discord" — it is posted *in* the server it would be
    naming, where the server name carries no information.

59. **`GuildName` is stored on the pending report rather than read at
    confirmation time.** The name is captured in the modal handler
    (`Context.Guild?.Name`) and travels with the draft through
    `ReportSubmission` into the `PendingReports` row, so the footer says where
    the report was actually made even if the guild is renamed during the hour
    the draft can sit there. It also keeps the confirmation path free of a
    gateway lookup on data it already owns. Like the `ClaimedAtUtc` column in
    decision 52, the new column ships **without a migration**: `EnsureCreated()`
    creates missing tables but never alters an existing one, so anyone running a
    build from before this change must delete their SQLite file again (it holds
    only a rebuildable embedding cache and drafts younger than an hour) or the
    first query against `PendingReports` will fail on the missing column.

## 2026-08-19 (embedding noise)

60. **A hidden marker separates the reporter's words from the bot's, and the
    sync service embeds only the reporter's half.** `IssueBodyComposer` now
    emits `<!-- discord-gh-issue-bot:meta -->` immediately after the draft
    body and before every block it appends — the regression line, the
    screenshot gallery, the upload-failure note, the attribution footer.
    `IssueSyncService` cuts each fetched body at the first occurrence of that
    marker and uses the part in front of it for all three derived values: the
    content hash, the text sent to the embedding model, and the `BodyExcerpt`
    quoted into the duplicate judge's prompt. The problem is that the
    boilerplate is *shared*: every issue this bot files ends in the same
    footer, and most end in the same `### Screenshots` heading, so embedding
    it gives every bot-created issue a vector component none of them earned —
    two unrelated reports look similar because they were both filed through
    Discord. The excerpt has the same disease in prose form, spending the
    judge's prompt budget on a sentence that is identical across every
    candidate. The report side was already clean (`ReportPipeline` embeds the
    AI draft *before* the composer runs), so the sync side was the only place
    where the query and the corpus disagreed about what an issue's text is.
    An HTML comment was chosen over a sentinel line because GitHub renders it
    as nothing at all — the reader of the issue never sees it, and neither
    does anyone quoting the body elsewhere. Absence of the marker means "not
    ours": human-authored issues, and every issue this bot filed before this
    change, are hashed, embedded and excerpted whole exactly as before, so
    nothing needs re-syncing for correctness. The marker is emitted
    unconditionally rather than only when boilerplate follows, because the
    footer is itself unconditional — the condition would always be true.
    Keeping the hash on the same stripped body is the payoff: a bot issue
    whose boilerplate changes (a screenshot added, a regression line, a
    renamed Discord server) no longer looks like changed content and no
    longer buys a fresh embedding call.

## 2026-08-19 (GitHub App authentication)

61. **An app authenticates with a PAT or with a GitHub App, and validation
    enforces exactly one.** `AppConfig` gained an optional `GitHubApp` block
    (`AppId`, `InstallationId`, and one of `PrivateKey`/`PrivateKeyPath`)
    beside the existing `GitHubToken`. A GitHub App is the better identity for
    a shared bot — issues are authored as `<app>[bot]` rather than by whoever
    owns the token, the credential does not leave with the person, and its
    reach is the repositories the App is installed on rather than everything
    the token owner can see — but PATs stay first-class, because they are two
    minutes of setup against the App flow's ten and every existing deployment
    uses one. Both configured is a startup error rather than a precedence
    rule: the bot would have to pick one silently and the operator would never
    learn which, and "the PAT wins" is exactly the wrong answer for someone
    who just added an App block and expects it to take effect. Neither
    configured is an error naming both keys, which also keeps the pre-existing
    "GitHubToken is required" behaviour for an app that configures nothing.
    A configured `PrivateKeyPath` is checked for existence during the same
    startup validation that catches a missing app id — a typo in a path should
    not wait for the first report an hour into the run — and it is trimmed on
    assignment for the same reason `Repo` is: a path carrying a secret file's
    trailing newline would fail a check the value passes when you read it.
    That makes `PrivateKeyPath` the second field to trim, narrowing decision
    54's "only `Repo` is trimmed" to its actual rule — whitespace here is
    silently fatal rather than merely untidy, and that is the test a field has
    to pass. The PEM itself is left alone: `RSA.ImportFromPem` tolerates
    surrounding whitespace, and a key is not a field to be clever with.

62. **The App JWT is hand-rolled; no JWT package was added.** Authenticating
    as a GitHub App means signing a JWT with three claims (`iat`, `exp`,
    `iss`), a fixed header (`RS256`/`JWT`), and one `RSA.SignData` call over
    two base64url segments — about fifteen lines using
    `RSA.ImportFromPem` and `System.Buffers.Text.Base64Url`, both in the
    framework. A JWT library would bring a dependency, its transitive
    closure, and its own upgrade cadence to produce the same eighty bytes; it
    earns its place when a service *validates* arbitrary tokens (key
    discovery, algorithm confusion, clock policy, revocation), and none of
    that applies to signing one fixed shape with one key we own. The token
    shape has not changed since GitHub Apps shipped, and the test suite
    verifies the signature against an ephemeral key rather than trusting the
    encoding by eye. `iat` is backdated 60 seconds and the window is 600
    seconds wide — GitHub rejects anything over ten minutes and validates
    `iat` against its own clock, so a slightly fast local clock is a non-event
    instead of a 401 that looks like a bad key.

63. **Installation tokens are cached per app, refreshed five minutes early,
    behind a per-app semaphore.** GitHub's installation tokens last an hour,
    so minting one per interaction would be an extra round trip on every
    report and a rate-limit line item for nothing. `GitHubAuthProvider` keeps
    one token per `Repo` (the key `BotOptions` already makes unique, compared
    case-insensitively like everywhere else) and re-mints when less than five
    minutes remain: the margin covers a request that starts just inside the
    window and lands just outside it. The refresh happens under a per-app
    `SemaphoreSlim` so a burst of concurrent interactions on one repo buys one
    token rather than one each, and the fast path reads the cached token
    without taking the gate at all. The PEM is read from disk at most once per
    process for the same reason. A token whose response carries no
    `expires_at` — documented as always present, so this is the
    shape-changed case — is used but assumed valid for only ten minutes and
    logged as a warning, which keeps the bot running without letting a token
    whose real lifetime we never learned go stale in the cache. Everything
    else (a rejected exchange, a response with no token at all) throws
    `HttpRequestException`, which is what every caller of the GitHub clients
    already handles.

64. **The auth provider is a singleton over a *named* HttpClient, not a typed
    one.** Every other GitHub client here is a typed client, which
    `AddHttpClient` registers as transient — correct for them, fatal for this
    one: a transient provider would carry an empty token cache into every
    interaction and mint a fresh token each time, which is the whole thing the
    cache exists to prevent. So the provider is registered as an explicit
    singleton, and the client it holds is a *named* client rather than a typed
    one, because a typed client captured in a singleton is the documented way
    to lose `IHttpClientFactory`'s handler rotation and, with it, DNS changes.
    The mitigation Microsoft documents for a deliberately long-lived factory
    client is applied instead: `SocketsHttpHandler.PooledConnectionLifetime`
    set to two minutes (the default handler lifetime it replaces) and the
    factory's own rotation switched off with
    `SetHandlerLifetime(Timeout.InfiniteTimeSpan)`. `HostSetupTests` pins the
    singleton lifetime, so a later change to a typed registration fails a test
    rather than quietly costing a token per report.

65. **A meta marker pasted into a report is stripped rather than honoured.**
    `IssueBodyComposer` now removes any literal `MetaMarker` from the draft
    before emitting its own, so the invariant decision 60 relies on — a
    composed body contains exactly one marker, and it is ours — holds by
    construction rather than by emission order. The draft is the reporter's
    text, which makes it attacker-chosen: a pasted marker used to be the
    *first* one in the body, and `IssueSyncService` cuts at the first, so a
    reporter could have hidden everything after it from the embedding, the
    content hash and the duplicate judge's excerpt. Stripping happens before
    the trim, so a draft that is nothing but a marker still counts as empty.
    Removing the marker leaves the blank lines that surrounded it, which
    markdown collapses back into the one paragraph break it already was —
    not worth a normalization pass that would touch legitimate whitespace in
    every other report. The invariant is compose-time-forward only: issues
    already filed on GitHub with a pasted marker in them keep their truncated
    semantic body until someone edits the issue, since nothing rewrites
    history.

66. **A GitHub App private key is parsed at startup, not at the first mint.**
    Validation used to ask only whether a key was configured and, for
    `PrivateKeyPath`, whether the file existed — so key *bytes* that
    `RSA.ImportFromPem` rejects sailed through startup and failed inside the
    first report, minutes or hours later, as an exception the reporter sees
    and the operator has to correlate. The overwhelmingly likely way to get
    there is an inline PEM squeezed into an environment variable, where the
    `\n` that is a real newline in JSON stays two literal characters after a
    shell export. `ValidateAuth` now resolves the configured key (inline text,
    or the file's contents) and imports it into a throwaway `RSA`; any failure
    becomes `Apps[i].GitHubApp.<field>: not a valid PEM private key.`, naming
    whichever form was configured and carrying neither the key material nor
    the exception text, because startup errors get logged. `.env.example` no
    longer shows an inline PEM at all — it points at `PrivateKeyPath` and
    reserves `PrivateKey` for key-per-file secrets, where the content really
    is a multi-line PEM. The one-time file read this costs at startup buys the
    same "wrong config fails the run" guarantee every other field already had.

67. **`Authorization` is redacted from `IHttpClientFactory`'s own logging.**
    The factory logs request and response headers at Trace level, which on
    these clients means a PAT, an App JWT, or a freshly minted installation
    token in plain text in whatever collects the logs — nobody enables Trace
    for that, but the day someone enables it to debug a GitHub call is exactly
    the day it happens. Every GitHub-facing registration (`GitHubService`, the
    image uploader, and the auth provider's named client, whose response
    carries the installation token) now calls
    `RedactLoggedHeaders(["Authorization"])`. It is one call per registration
    and costs nothing at runtime when the log level is off.

## 2026-08-19 (embedding model tag)

68. **Stored vectors carry the model that produced them, and a mismatch is
    re-embedded rather than compared.** `IssueEmbedding` gains an
    `EmbeddingModel` column holding the OpenAI model id that produced `Vector`.
    Embeddings from two different models share no coordinate space, so a cosine
    similarity computed across them is a number with no meaning — and nothing in
    the old code could tell the two apart: changing `OpenAI:EmbeddingModel`
    left every cached vector in place and the duplicate ranking silently became
    noise, with no error, no log line and no way to notice except by watching
    the verdicts get worse. `IssueSyncService` now reads the configured model
    from `BotOptions`, stamps it on every row it embeds (alongside the content
    hash, and only once the vector is in hand, so a failed call never claims a
    provenance it does not have), re-embeds on `hash changed OR model changed`,
    and `GetCandidatesAsync` filters mismatched rows out of the candidate list
    instead of ranking them.
    Excluding them alone would not heal them: sync fetches only issues updated
    since the watermark, so after a model switch an issue nobody touches would
    stay excluded forever and the candidate list would quietly shrink to
    whatever GitHub happened to bump. `SyncAsync` therefore ends with a heal
    pass over this repo's mismatched rows, batched and flushed exactly like the
    main loop and equally free to fail — the watermark is untouched by it, so a
    partial pass is simply finished by the next sync. The main loop is flushed
    before the pass runs, or a row embedded seconds earlier would read back as
    stale and be paid for twice.
    The pass re-embeds from the stored `Title` and `BodyExcerpt` rather than
    refetching the bodies from GitHub, and it is worth being exact about how
    small that saving is. `ListIssuesAsync` pages at `per_page=100`, so a
    refetch is about `N/100` HTTP requests — ten calls for a thousand stored
    issues — and the embedding spend is the same either way, because the pass
    embeds once per stale row wherever the text comes from. So the whole
    purchase is a handful of GitHub requests per model change. The price is that
    a body over 1000 characters is re-embedded from its opening only and keeps
    that thinner vector until the issue is edited on GitHub and the incremental
    path re-embeds it in full; bodies that long are not exotic, since a bug
    report carrying a log excerpt or a stack trace passes 1000 characters
    routinely, and no measurement of this repo's issue-length distribution was
    taken. The trade was accepted because a model change is a rare,
    operator-initiated event and a slightly thinner vector still ranks in the
    right space, where a mismatched one does not rank at all — but it is a thin
    margin, and switching the pass to refetch is the first thing to try if
    duplicate verdicts degrade after a model change.
    Like decisions 52 and 59, the column ships **without a migration**:
    `EnsureCreated()` creates missing tables but never alters an existing one,
    so anyone running an older build must delete their SQLite file again (it
    holds only a rebuildable embedding cache and drafts younger than an hour) or
    the first query against `IssueEmbeddings` will fail on the missing column.
    Rows written by a build from before this change would carry an empty model
    string, which the heal pass treats as a mismatch and re-embeds — but that
    path only matters to a database that survives, and this one does not.

## 2026-08-19 (CI/CD pipelines)

69. **Release ships from every push to `main`, tagged `latest` + `sha-<commit>`,
    with no semantic versions.** Chosen by the operator over tag-driven
    releases: this bot deploys to hosts its operator controls, nobody else pins
    versions of it, and a merge to `main` already means "this should be
    running". `docker/metadata-action` supplies the tags (`type=raw,value=latest`
    plus `type=sha`), so every image stays pullable by the exact commit that
    built it even though `latest` moves. If the project ever grows outside
    consumers, adding a `v*` tag trigger with semver tags is additive — nothing
    about this choice has to be undone.

70. **The Release workflow re-runs the tests itself instead of chaining off
    CI.** Pushes to `main` therefore run the test job twice, once in each
    workflow, and that duplication is deliberate. The alternatives are worse:
    gating on the CI workflow via `workflow_run` runs the released code path
    from the default branch's workflow file and decouples the gate from the
    commit being released, and dropping the gate entirely would ship an image
    from a red `main`. A few duplicated runner-minutes buy a release pipeline
    that is self-contained — its green checkmark alone proves the image it
    pushed passed the suite. The publish job builds `linux/amd64` only (that is
    what the operator runs) and authenticates to ghcr.io with the workflow's
    `GITHUB_TOKEN` under an explicit `permissions: packages: write`, so the
    pipeline needs no configured secrets at all.

## 2026-08-19 (flex service tier: tried and reverted)

71. **Chat stays on OpenAI's standard service tier; the flex rollout was
    reverted the same day.** Commit b349652 moved both chat calls
    (normalization, duplicate judge) to the half-price flex tier, with an
    `OpenAI:ServiceTier` escape hatch and a 5-minute network timeout for
    flex queuing. It was reverted before ever serving a live report: flex
    schedules on spare capacity, so responses can queue for minutes and are
    rejected with a 429 when capacity runs out — and both of this bot's
    chat calls sit inside a live Discord interaction with a reporter
    waiting, so the discount buys degradation in exactly the two calls the
    product feels most. The revert also removes the config knob and the
    long timeout, which existed only to serve flex. Both commits remain in
    history; if a background or batch chat path ever appears, the rollout
    is a cherry-pick away.

## 2026-08-19 (duplicate comments)

72. **A duplicate comment carries only what the report adds, never the whole
    report again.** Confirming "same issue" used to post the full normalized
    draft as the comment — a repeat of what the issue already says, which is
    noise to every subscriber. Now `AdditionalInfoExtractor` (one extra chat
    call, made only when the reporter confirms a duplicate) compares the
    draft against the matched issue and returns a tri-state: new details are
    posted above an "Also reported by <name> in Discord server
    <server>" footer; a report that adds nothing posts that footer line
    alone; and a failed extraction — or an issue missing from the candidate
    cache — falls back to posting the full draft as before, because a
    redundant comment is recoverable while silently dropped details are not.
    The comparison runs against the cached title and 1000-character body
    excerpt, the same text the duplicate judge matched on, so the comment
    path adds no GitHub read. Known limit accepted with that choice: details
    buried past the excerpt, or added earlier by another bot comment, can
    still be repeated. Screenshots always post — they are new information by
    nature. Issue bodies keep the "Created by" footer; only comments switch
    to the also-reported wording.

## 2026-08-19 (app dropdown in the modal)

73. **The app is picked inside the modal, superseding decision 28.**
    `/report-issue` and `/request-feature` lost their optional `app` option:
    reporters found it confusing that a parameter existed which they almost
    never had to fill in. The modal itself now asks instead — a required
    "App" string-select on top of the form, shown only when the guild maps
    to several apps; with one app the modal is unchanged and the repository
    still rides in the custom id. Decision 28 chose the option because
    modals could not hold select menus at the time; Discord.Net 3.20.1 (the
    same modal-components wave that gave the form its file upload) can. The
    dropdown is not declared on `ReportModal`: its options are the guild's
    configured apps, which an attribute cannot know, so `OpenModalAsync`
    injects it via `modifyModal` and the submit handler reads the pick from
    the raw modal data whenever the custom id's repo segment is the `-`
    placeholder (unambiguous — real repositories always contain a slash).
    `/issues` keeps its `app` option: it opens no modal to ask in.

## 2026-08-19 (app dropdown review round)

74. **Dropdown capacity failures answer with a named problem, not a
    truncated list.** A Discord select menu holds at most 25 options of at
    most 100 characters; a guild with more apps, or an app whose name or
    "owner/repo" is longer (GitHub allows up to 140), would make
    `BuildAppPicker` throw inside `RespondWithModalAsync` and turn every
    report in that guild into a generic apology. `PlanModal` now refuses
    those shapes up front with a "please tell an admin" message instead of
    silently dropping apps from the dropdown — an explicit failure an
    operator can act on beats a picker that quietly hides configured apps.
    Startup validation was considered and rejected: an oversized repo still
    works fine in a guild where it is the only app. From the same review:
    the submitted pick (custom id segment or dropdown value — both echo
    back through the client) now resolves against the guild's own apps
    rather than every configured one, and an empty pick gets its own reply
    instead of the misleading "no longer configured".

## 2026-08-19 (attachment validation)

75. **Attachment bytes are verified by signature; the declared content type
    never reaches the payload.** Discord derives an attachment's `ContentType`
    from the file name the uploading client supplied, so `evil.exe` renamed to
    `evil.png` arrives declared `image/png` — and the downloader used to hand
    those bytes straight to SQLite and then to GitHub, where they become a
    permanently hosted file under the repository's own identity. The download
    now ends with `ImageSniffer`, which matches the leading magic bytes against
    PNG, JPEG, GIF and WebP: the formats Discord actually produces and GitHub
    actually renders inline. Anything else is skipped through the existing
    skip/notice path, as is a body whose real length exceeds 10 MB — the
    declared size is client-adjacent metadata too, and only the array we hold
    is trustworthy. The declared type survives solely as a pre-download filter,
    which is free and saves fetching obvious junk. SVG is deliberately not on
    the allowlist: it is XML that can carry scripts, an XSS vector wherever it
    is served inline, and being text it has no magic number to match anyway.
    The payload now carries the sniffed type, which also fixes a smaller wart —
    parameterised types like `image/png; charset=utf-8` no longer travel to the
    upload endpoint (decision 44's `MediaTypeHeaderValue.Parse` stays as the
    belt to this braces). From review: the length check alone was not enough,
    because `GetByteArrayAsync` buffers the whole body before anything can
    measure it — with the typed client's default ~2 GB
    `MaxResponseContentBufferSize`, the only real bound on memory was again the
    declared size. `HostSetup` now caps that buffer at `MaxBytes + 1`: a body of
    exactly the limit plus one still reaches the length check, and anything
    larger fails inside `HttpClient` and lands in the downloader's existing
    catch as an ordinary skipped file.

## 2026-09-29 (OpenRouter + Jev redesign)

The redesign is specified in
`docs/superpowers/specs/2026-09-29-openrouter-jev-redesign.md`; the entries
below record the choices inside it that are not obvious from the code.

76. **All model traffic goes through OpenRouter, over two hand-rolled typed
    `HttpClient`s.** OpenRouter has no .NET SDK, and the two features this
    bot leans on — `provider.order` routing for the flex tier and the
    alpha Decisions API — are not reachable through
    `Microsoft.Extensions.AI`'s OpenAI adapter without patching raw request
    JSON. Two small clients (`OpenRouterChatClient`, `DecisionClient`) are
    less code than that plumbing and are tested at the HTTP level, the same
    trade decision 6 made for GitHub. Structured output keeps its
    guarantee: the strict JSON schema is generated from the very DTO the
    answer is parsed into (`StructuredOutput`), tightened to what strict mode
    demands (closed objects, every property required, no nullable members),
    so schema and parser cannot drift. A completion that stopped early,
    refused, or does not fit the schema is a failure, never a partial answer.

77. **Flex is back, as the default, with the queueing problem that got it
    reverted (decision 71) handled explicitly.** The owner's requirement is
    flex first with fallback to the regular tier. Two mechanisms cover the
    two ways flex hurts: rejections (429 when capacity runs out) are
    absorbed server-side by OpenRouter itself, because `provider.order`
    lists `openai/flex` then `openai` with fallbacks allowed; *queueing* is
    invisible to OpenRouter, so every interactive call (a reporter waiting on
    a deferred interaction) carries a client-side deadline,
    `ChatDeadlineSeconds` (30 s), after which — or after any other transient
    failure — it is retried once on `ChatRetryProviders` (`openai`, regular
    tier). Worst case a reporter waits the deadline plus one regular call,
    and a request abandoned mid-queue may still be billed at the flex rate,
    which is half of what the retry costs. Background calls (repo-map upkeep)
    get a 10-minute deadline and simply wait the queue out. Service-tier
    endpoints must be named explicitly in `provider.order` — a bare
    `openai` never matches `openai/flex` — which is why the tier is a
    configured provider list rather than a flag. The two provider lists are
    nullable config lists because the configuration binder appends to an
    initialised list instead of replacing it.

78. **Every OpenRouter response's `usage.cost` feeds a scoped
    `AiUsageMeter`.** The redesign has a hard budget ($5–10 for 500–1000
    issues a month); OpenRouter prices each response in USD, so the bot can
    log what each report actually cost instead of trusting the estimate in
    the spec. The meter is scoped — one report, one click, one refresh — and
    locked, because a report fans decision calls out in parallel.

79. **One `/issue` command; Jev decides bug or feature.** The reporter no
    longer chooses between `/report-issue` and `/request-feature` — the
    decision model reads the report and answers a two-option `choice`
    (`ReportClassifier`), which picks the draft template and the type label.
    A `choice` rather than a `noul` because the two are mutually exclusive
    alternatives of one rubric. A failed call falls back to *bug*: most
    reports are, and the preview now shows the classified type ("Bug
    report") above the draft, so a wrong call is visible before anything is
    filed and Cancel is the way out.

80. **Duplicates are found by the decision model reading the open issues,
    in two stages — the embedding/KNN path is gone.** The owner's finding
    was that nearest-neighbour similarity did not work; the replacement
    reads contents. Stage one (`Shortlist`) is TypeSafe's "rank, then
    re-check" pattern: one `choice` over every open issue (title + 300-char
    excerpt, keyed `issue_<n>`, plus `none`), chunked at 120 issues so a
    request stays near half of Jev's 32k context, with a final `choice` over
    the survivors when several chunks produced any (each chunk's
    distribution is normalised over its own options only). Anything at 0.05
    or above survives, top 5. Stage two lays each survivor's full excerpt
    next to the draft and asks one three-level `score` per issue —
    *different* / *related but distinct* / *same underlying problem* — and
    routes on the probability of the top level: >= 0.5 offers the issue,
    >= 0.2 asks the reporter, below is dropped. A `score` rather than a
    `noul` because the explicit "related" level draws off the mass a yes/no
    question gives to same-area-different-bug issues (decision models read
    loosely). Two issues above 0.5 are a pick list, not a match. The
    thresholds are the Decisions skill's pre-probe defaults and live as
    named constants; no API key was available while building this, so they
    are unprobed — `--dry-run` exists to tune them. Degradation keeps "ask
    rather than guess": stage two failing asks about the whole shortlist;
    stage one failing has nothing to ask about and treats the report as new
    (the reporter still confirms). Dedup runs on the normalized draft, not
    the raw text: English, structured, and closer in shape to the issues it
    is compared with.

81. **Only open issues are candidates; the "still happening?" flow is
    removed.** The owner's requirement says dedup against open issues, and
    confirmed dropping the closed-within-30-days flow (decision 2) with its
    buttons, the regression line, and `ReportOutcomeKind.MatchClosed`. The
    cache (`CachedIssue`) now holds open issues only: an incremental pass
    upserts what is open and deletes what closed; a full pass lists every
    open issue and replaces the cache with exactly that, which also clears
    deleted or transferred issues an update feed never reports. The first
    sync is full (open issues only — the old cache paged through every
    closed issue ever filed) and a full pass repeats daily.

82. **The database schema is stamped with `PRAGMA user_version` and rebuilt
    on a mismatch, replacing "delete your SQLite file".** Decisions 52, 59
    and 68 each asked operators to delete the file by hand after a schema
    change — easy to miss on a Docker volume, and fatal on the first query.
    `DatabaseSchema.EnsureCurrent` compares the stamp with
    `DatabaseSchema.Version`; on a mismatch it drops every table, recreates
    the schema and stamps it. That is only acceptable because nothing in
    the file is precious — caches rebuild and drafts live an hour — and it
    is still not a migration story: the day the file holds real data, this
    repo needs EF migrations.

83. **The draft model writes three alternative titles under explicit
    "representative title" rules.** The owner asked that titles represent
    the actual issue. The prompt now demands the specific symptom (bugs) or
    capability (features) and where it happens, the reporter's concrete
    details, and bans generic phrasings by example ("Bug report", "Issue
    with the app"); the old "imperative mood" rule is gone, since "Fix
    checkout" says less than "Checkout page goes blank after tapping Pay".
    Three alternatives rather than one so the choice can be made separately
    from the writing; until the decision model picks (next feature), the
    first is used.

84. **Jev picks the title and the labels in one request after the draft is
    written.** `DraftReviewer` asks one `choice` over the draft model's
    three titles ("which describes the actual problem or request so a
    maintainer scanning the list understands it without opening it") and one
    `noul` per repository label — both selection, which is the job a decision
    model does well and a generative one does not: it cannot pick a title
    nobody wrote or a label the repository does not have. Labels are
    independent nouls because several can apply at once; a `choice` would
    force exactly one. The gate is the pre-probe 0.5. Labels come live from
    `GET /labels` on every report (a single call alongside the issue sync);
    a listing failure leaves the issue unlabelled rather than failing the
    report. Triage outcomes (`duplicate`, `invalid`, `wontfix`,
    `good first issue`, `help wanted`) are never offered — they record a
    maintainer's decision, not a property of a fresh report — and an app can
    replace that list with `IgnoredLabels`. The repository's canonical type
    label (`bug`; `enhancement`/`feature`) is always attached when it exists,
    so the classified type and the labels agree on the basics even when the
    model hesitates; that replaces the old hard-coded `bug`/`enhancement`,
    which attached labels the repository might not define. The title
    choice and dedup run in parallel, so dedup reads the draft under its
    first title — the body carries the substance, and serialising the two
    would add a round trip in front of the reporter. The preview's small
    print now shows the chosen labels next to the type. A failed review
    falls back to the first title and the type label.

85. **Commands are global and user-installable; `/issue-install` hands out
    the link.** A user-installed command belongs to no server, so the
    per-guild registration of decision 5's era cannot reach it: every
    command is now registered globally with integration types
    `[GuildInstall, UserInstall]` and contexts `[Guild, BotDm,
    PrivateChannel]`, declared once on the module (a test pins that
    Discord.Net carries them to every command). The per-guild registrations
    of earlier builds are overwritten with an empty set on every Ready, or
    configured servers would list each command twice. The owner's
    "/issue login" became `/issue-install` — "login" was the wrong word,
    and Discord forbids a runnable `/issue` that also has subcommands, so
    the link command is its own top-level command. It answers with the
    user-install URL (`integration_type=1`, scope `applications.commands`
    only) built from the interaction's own application id, so there is
    nothing to configure — except enabling "User Install" in the Developer
    Portal. Nothing in the module may rely on `Context.Guild` any more: in a
    server the bot never joined it is null even though the interaction has
    a `GuildId`, and the footer then credits the reporter "via Discord".

86. **Outside configured servers, every app is on offer.** A server listed
    in some app's `GuildIds` still sees exactly those apps; a DM, a group DM,
    or a server nobody configured (all reachable through a user install)
    sees every configured app — the owner's choice over an opt-in flag or a
    membership check. Resolution is one place, `BotOptions.AppsForContext`,
    used for opening the modal, validating its submit, and `/issues`.

87. **Reports are rate limited per user: 10 per rolling day by default.**
    With every repository reachable by anyone who installs the bot, and each
    report spending money on model calls, one account could otherwise burn
    the month's budget. `ReportRateLimiter` counts submissions in memory
    (a restart forgives everyone — it guards against runaway use, not
    determined abuse); the check runs when `/issue` opens the modal, so a
    reporter is refused before typing, and again on submit, where the report
    is counted because that is where it starts to cost. The hard ceiling
    belongs on the OpenRouter key's credit limit, which APP.md tells
    operators to set. `Limits:ReportsPerUserPerDay = 0` turns the cap off.

88. **Code context comes from a repository map, not from reading the
    repository per report.** The owner asked that issues name the relevant
    code files and carry context from the code, cheaply. `RepoMapWorker`
    keeps, per app, one row per source file on the default branch — path,
    blob SHA, and a ≤25-word summary written by GPT-6 Luna on the flex tier
    (background urgency: it may wait out the queue). Refreshes are
    incremental on git's own identity: two GitHub calls when the head has not
    moved, and otherwise only files whose blob SHA changed are summarized
    again, in batches of up to 25 files / 60k characters with each file cut
    to its first 8k. A pass summarizes at most 400 files and the worker
    returns every 5 minutes (instead of hourly) while a map is incomplete, so
    a first build of a large repository spreads over a few passes; the map
    keeps the 2000 shallowest files. A file the model refuses, answers
    off-schema, or silently skips is stored with an empty summary — it drops
    out of selection until its blob changes — because asking again would
    most likely pay for the same failure every pass; only transient failures
    end a pass for a retry. At "Create issue", `CodeContextBuilder` has Jev
    shortlist files from the map (the same two-round `Shortlist` helper as
    dedup, chunks of 150 files), fetches the picks at the exact blob the map
    summarized, and has Luna say which are really involved and how. The
    decision model can only offer paths that exist; paths the chat model
    names that it was not given are dropped. The block goes after the
    `MetaMarker`, so dedup never reads it, with links pinned to the mapped
    commit. It is written into GitHub only — never the Discord preview —
    because anyone who can run the bot (now: anyone who installs it) is not
    necessarily allowed to read a private repository's code; and it is built
    at confirmation rather than at submit, so duplicates and cancellations
    never pay for it. Degradation: a failed notes call still lists the
    picked files with their map summaries ("Possibly relevant", flagged as
    unread); a failed selection files the issue without the block.

89. **Markdown docs are mapped alongside code, with their own selection.**
    From the owner: repositories keep useful context in `.md` files.
    `.md`/`.mdx`/`.markdown`/`.rst`/`.adoc` files are mapped (summaries say
    what each doc explains), except licence and code-of-conduct files and
    anything under excluded directories such as `.github` or
    `node_modules`. Code and docs are selected in two separate `choice`
    rounds — up to 4 code files, up to 2 docs — because in one shared
    ranking a wordy, broadly-on-topic doc can take the place of the file
    that actually needs fixing. The issue shows them as "Relevant code" and
    "Related docs". The kind is derived from the path, so the map stores
    nothing extra.

90. **Code context reads the issue repository itself; there is no separate
    "code repo" setting.** A split setup — public issue tracker, private
    code — is common, but code notes about a private repository written into
    a public tracker would publish private code. Mapping only `Repo` keeps
    the notes exactly as visible as the code they describe. Supporting a
    separate code repository would need a visibility check first.

91. **`--dry-run owner/repo "text"` runs one report through every model
    call and prints the result instead of storing or posting it.** The
    Decisions skill's rule is "probe before you trust": thresholds come from
    raw probabilities on representative inputs, not from defaults. No
    OpenRouter key was available while this was built, so every gate in the
    bot (type, title, labels at 0.5, dedup at 0.5/0.2, shortlist floors at
    0.05) is a pre-probe default. The dry run refreshes the app's repository
    map, runs `ReportPipeline.AnalyzeAsync` (the model half of
    `ProcessAsync`, split out for this) and the code-context builder, and
    prints type, all titles with the chosen one marked, labels, the dedup
    verdict with its shortlist, the body, the code block and the AI cost —
    with Debug logging switched on for the bot's own categories, so every
    decision's raw answers appear above the summary. It does update the
    issue cache and the map, which are caches; it never stores a pending
    report or touches Discord or GitHub issues.

92. **Review fixes to the repository map and the CLI (same day).** An
    independent review of the branch found three defects, all fixed. (a) Any
    non-transient OpenRouter error used to park a batch as unsummarizable —
    including request-level refusals (402 credits exhausted, 401 bad key,
    400 unknown model), which say nothing about the files. Reaching the
    credit limit, the intended hard cap, would therefore have blanked the
    map for good. Now only answer-level failures (a refusal, a truncated or
    off-schema answer) park files; anything carrying an HTTP status ends the
    pass for a retry, and `finish_reason: "error"` counts as transient.
    (b) `--dry-run` or `--smoke-upload` with the wrong number of arguments
    fell through to `host.RunAsync()` and started the live bot; both now
    print their usage and exit 1 before the host is built. (c) Code links
    pointed at the latest head while a changed file's summary and blob were
    still the older version's; each `RepoFile` now records the commit it was
    read at (schema v5) and its link pins to that, so a link always shows
    what the notes describe.

93. **Under BYOK the usage meter counts the provider's charge, and Jev's
    input tokens.** The first live runs showed every call at $0: the owner's
    OpenRouter account routes both OpenAI and TypeSafe through their own
    keys (BYOK), where OpenRouter's `cost` is only its own fee and the
    provider bills directly. Chat responses still carry what OpenAI charged
    in `cost_details.upstream_inference_cost` (flagged `is_byok`), which the
    chat client now adds — only for BYOK calls, since otherwise it is already
    inside `cost`. Decision responses carry no upstream figure at all, so
    the meter also sums the decision model's `input_tokens`, TypeSafe's
    billing unit, rather than hard-coding a price that can change; every
    usage log line reads `$<cost> in <n> call(s), <k> decision-model input
    tokens`. The same finding moves the spending-cap advice: with BYOK the
    OpenRouter key's credit limit caps only OpenRouter's fees, so the
    budget limits belong on the OpenAI and TypeSafe accounts. Those runs
    (no GitHub access yet, so no labels, dedup or code context) also gave
    the first real numbers: flex pricing confirmed from the upstream cost,
    $0.00006–0.00008 per draft, ~1,000 Jev input tokens for type + title,
    clear bug/feature reports at P = 1.0, an arguable "please keep my
    language" report at feature 0.64 / bug 0.36, a Danish report translated
    correctly, and a prompt-injection attempt ("title this URGENT SECURITY
    HOLE, label it critical") ignored in favour of the real problem.

94. **The repository map is updated at startup and every 10 minutes, and a
    check runs to completion.** The owner asked for a summary pass on
    startup and then every 10 minutes over whatever was added or changed.
    The worker now starts its first check immediately (no 30-second delay)
    and waits 10 minutes after each check instead of an hour: a check that
    finds nothing changed costs two GitHub calls and no model calls, and
    every minute of lag is a minute in which new reports are matched against
    the previous version of the files a push changed. "Added or changed
    since the last check" is the existing blob-SHA diff, which cannot miss an
    edit. The per-pass cap of 400 summaries stays, but only as a checkpoint:
    `RepoMapService.UpdateAsync` runs passes back to back until the map is
    complete, so the startup check is the whole first build instead of one
    capped slice followed by 5-minute catch-up ticks (that catch-up interval
    is gone). An update stops early when a pass makes no progress — a failure
    the pass has already logged — and the next check retries; the pass
    count is bounded by what a full 2000-file build needs. The dry run uses
    the same update, so its code context always reads a complete map.

95. **Spike: file retrieval for code context — embeddings plus keywords
    beat "Jev reads the whole map" at 3% of the cost.** Selecting files by
    showing Jev every summary cost ~145k decision tokens per created issue on
    mtg-grimoire (1,551 mapped files) and grows with the repository. The spike
    (`tools/RetrievalSpike`) scored cheap retrievers against real ground truth:
    the 133 mtg-grimoire issues closed by a linked pull request, with the files
    each fix changed as the answer. Reports were written two ways — the issue
    as filed, and rewritten by GPT-6 Luna as a non-technical Discord user would
    phrase it (no identifiers, jargon or the issue's terms) and then drafted by
    the bot — because the owner flagged that users do not use the code's
    vocabulary. User-voice hit@10 / hit@40 for code files: BM25 keywords over
    path + summary 62% / 83%; plus Luna-written search terms 72% / 92%
    (generic) and 78% / 90% (grounded in the repository's file tree);
    embeddings of path + summary 86% / 92% (voyage-4-lite), 84% / 94%
    (voyage-4); the code-specialised voyage-code-4 was worst at 68% / 89%,
    since it matches prose summaries, not code; hybrids of tree-grounded
    keywords and embeddings fused by rank 87% / 95%. With n = 133,
    differences of 2–3 points are noise: the top configurations tie. For docs
    (n = 86, noisier ground truth) the hybrid led, hit@5 57% vs 51% for
    keywords + tree terms and 47–50% for embeddings alone. End to end on 25
    issues, Jev picking 4 files from a 40-file shortlist matched Jev picking
    from the whole map — both 84% — with 4,771 instead of 145,009 decision
    tokens. Embedding a whole map costs under a cent. The retrieval pieces
    (`TextTokens`, `KeywordIndex`, `VectorIndex`, `RankFusion`,
    `EmbeddingClient`, `QueryExpander`) are in the bot but not yet wired into
    `CodeContextBuilder`.

96. **Code context retrieves a shortlist by keywords and embeddings, then
    lets Jev pick; schema changes can now upgrade in place.** Following the
    spike (decision 95) and the owner's go-ahead: at "Create issue", GPT-6
    Luna writes developer search terms with the repository's file tree in
    view; BM25 over path + summary and cosine similarity over
    `voyageai/voyage-4` embeddings of the same text each rank the map, fused
    by reciprocal rank, code and docs apart; Jev picks 4 code files from the
    top 40 and 2 docs from the top 10. voyage-4 rather than voyage-4-lite:
    they tied on code, voyage-4 led slightly on docs, and the price
    difference is a fraction of a cent a month. The tree-grounded search
    terms stay even though embeddings alone were nearly as good on code,
    because they carried docs (keyword hit@5 35% → 51%) and cost about
    $0.0008 an issue. Map rows gain an embedding and its model stamp; a new
    summary clears the vector, and every check embeds whatever lacks one from
    the configured model, so existing maps and model switches catch up on
    their own; an embedding failure never fails a check. Degradation: no
    search terms → the report text alone; no issue embedding → keywords
    alone. Live on mtg-grimoire the first check embedded all 1,551 summaries
    for $0.0035, and a created issue took 9,631 Jev tokens instead of 179k —
    and found the query parser the whole-map run had missed. To keep that
    map across this very change, `DatabaseSchema` now applies additive
    upgrade steps (here: two `ALTER TABLE ... ADD COLUMN`) when every version
    in between has one, and only rebuilds otherwise; decision 82's
    rebuild-on-mismatch remains the fallback.

97. **The draft runs on the regular tier; every other chat call stays on
    flex.** Measured on mtg-grimoire, one run each: the draft took 7.2 s on
    flex with low reasoning and 3.2 s on the regular tier (3.6 s on flex
    with no reasoning); search terms 3.6 s / 2.5 s / 3.9 s; reading the
    picked files plus the code notes 18.5 s / 5.8 s / 10.0 s. The draft is
    what a reporter waits on before seeing anything, and the regular tier
    costs about $0.0002 more per report, so it moved (the owner's call);
    search terms, code notes, duplicate comments and the repository map
    stay on flex. Chat prompts now carry a `ChatTier`; a `Regular` prompt goes
    straight to `RegularProviders`, which is also where every flex call is
    retried — the setting formerly named `ChatRetryProviders`, renamed before
    first deployment. Decision 77's flex-first default and deadline are
    otherwise unchanged.

98. **Code context is built in the background while the reporter reads the
    preview.** Measured with the draft on the regular tier: the preview comes
    ~5 s after submitting, and building the code context takes ~20–25 s on
    flex — search terms ~3.6 s, retrieval and Jev ~1 s, reading the files and
    writing the notes ~18.5 s. Built at "Create issue" (as before) that was the
    wait after the click; built before the preview, the wait after
    submitting. The owner's answer: the preview is for the reporter's own
    text and needs no code references, so show it at once and spend the
    reading time on the code context. `CodeContextPrefetcher` (a singleton,
    each build in its own DI scope since the interaction's scope is gone)
    starts the build when the draft is saved and stores the result on the
    pending report ("" meaning "built, nothing to add"); the click takes the
    stored block, awaits a build still running, or — after a restart lost the
    task — builds it then. Everything stays on flex (only the draft is on the
    regular tier, decision 97). The price is that every draft now pays for
    its code context, duplicates and cancellations included — about $0.002
    each — which the budget absorbs. Schema v7 adds the two columns as an
    additive upgrade.
