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
   Contents API on an orphan `issue-assets` branch if that fails. Discord CDN
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
    disguised as a normalization failure. Both services read model output with
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
    Image `FileName`/`Url` are interpolated into `![name](url)` without
    markdown escaping: both values originate from our own upload step
    (Task 6), which controls the names and returns GitHub-hosted URLs, and
    escaping them would corrupt legitimate URLs for no gain.

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
    finish the in-flight interaction. Verified with a flaky embedder plus an
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
    landing at the same instant can still both get through; the pipeline settles
    that race by itself, because the first one to finish deletes the pending
    report and the second then fails with `ExpiredPendingReportException`, which
    every handler turns into the "this report session has expired" reply.

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
