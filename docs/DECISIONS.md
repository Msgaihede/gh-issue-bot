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
    the modal.** The design spec sketched an app selector as the modal's first
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
