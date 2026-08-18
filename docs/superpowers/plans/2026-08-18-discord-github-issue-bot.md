# Discord → GitHub Issue Bot Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A .NET 10 Discord bot that AI-normalizes user bug/feature reports, dedupes them against GitHub issues (embeddings + LLM verdict), and creates/comments on GitHub issues after user confirmation, with screenshot upload.

**Architecture:** Single worker project on the Generic Host; Discord.Net gateway in a `BackgroundService`; EF Core + SQLite for embeddings cache and pending-report state; plain `HttpClient` for GitHub; `Microsoft.Extensions.AI` for chat + embeddings. All logic behind interfaces in `Ai/`, `GitHub/`, `Pipeline/`, `Data/`; the Discord layer stays thin.

**Tech Stack:** .NET 10, Discord.Net 3.20.1, Microsoft.Extensions.AI 10.9.0 (+ .OpenAI), OpenAI 2.13.0, EF Core Sqlite 10.0.11, System.Numerics.Tensors, XUnit + NSubstitute.

**Spec:** `docs/superpowers/specs/2026-08-18-discord-github-issue-bot-design.md` — read it before starting any task.

## Global Constraints

- TargetFramework `net10.0`, `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>`, file-scoped namespaces rooted at `DiscordGithubBot.*`.
- Pinned packages: Discord.Net **3.20.1**; Microsoft.Extensions.AI **10.9.0**; Microsoft.Extensions.AI.OpenAI **10.9.0**; OpenAI **2.13.0**; Microsoft.EntityFrameworkCore.Sqlite **10.0.11**; Microsoft.Extensions.Configuration.KeyPerFile **10.0.11**; Microsoft.Extensions.Hosting **10.0.x**; System.Numerics.Tensors **10.0.x**. If a pinned patch version does not exist on nuget.org, install the latest patch of the same major.minor and record the substitution in `docs/DECISIONS.md`.
- Chat model default **`gpt-5.6-luna`** — NEVER the bare alias `gpt-5.6` (routes to the ~10x-cost Sol tier). Embedding model default **`text-embedding-3-small`**, dimension 1536 (constant `VectorRanker.EmbeddingDimensions`, defined once).
- All timestamps stored/compared as UTC. All Discord replies to the invoker are ephemeral. Discord CDN URLs are never written into GitHub issue bodies.
- Run `dotnet build` and `dotnet test` before every commit; both must be clean.
- Commit after each task with a descriptive conventional-commit message.
- Update `docs/DECISIONS.md` whenever a task deviates from this plan or the spec.
- Do not add packages beyond those listed in the task that introduces them.

---

### Task 1: Solution scaffold, docs, hygiene files

**Files:**
- Create: `DiscordGithubBot.sln`, `src/DiscordGithubBot/DiscordGithubBot.csproj`, `src/DiscordGithubBot/Program.cs` (stub), `tests/DiscordGithubBot.Tests/DiscordGithubBot.Tests.csproj`, `tests/DiscordGithubBot.Tests/SmokeTests.cs`
- Create: `.gitignore`, `.env.example`, `src/DiscordGithubBot/appsettings.json`
- Create: `APP.md`, `CLAUDE.md`, `docs/DECISIONS.md`

**Interfaces:**
- Consumes: nothing (first task).
- Produces: compiling solution `DiscordGithubBot.sln` with `src/DiscordGithubBot` (console app, all pinned runtime packages installed) and `tests/DiscordGithubBot.Tests` (xunit + NSubstitute, project reference to src). Later tasks add files to these projects and never touch the csproj files except where a task says so.

- [ ] **Step 1: Create solution and projects**

```bash
cd /d/Code/discord-gh-issue-bot
dotnet new sln -n DiscordGithubBot
dotnet new console -n DiscordGithubBot -o src/DiscordGithubBot -f net10.0
dotnet new xunit -n DiscordGithubBot.Tests -o tests/DiscordGithubBot.Tests -f net10.0
dotnet sln add src/DiscordGithubBot tests/DiscordGithubBot.Tests
dotnet add tests/DiscordGithubBot.Tests reference src/DiscordGithubBot
```

- [ ] **Step 2: Add packages**

```bash
dotnet add src/DiscordGithubBot package Discord.Net --version 3.20.1
dotnet add src/DiscordGithubBot package Microsoft.Extensions.Hosting --version 10.0.11
dotnet add src/DiscordGithubBot package Microsoft.Extensions.AI --version 10.9.0
dotnet add src/DiscordGithubBot package Microsoft.Extensions.AI.OpenAI --version 10.9.0
dotnet add src/DiscordGithubBot package OpenAI --version 2.13.0
dotnet add src/DiscordGithubBot package Microsoft.EntityFrameworkCore.Sqlite --version 10.0.11
dotnet add src/DiscordGithubBot package Microsoft.Extensions.Configuration.KeyPerFile --version 10.0.11
dotnet add src/DiscordGithubBot package Microsoft.Extensions.Http --version 10.0.11
dotnet add src/DiscordGithubBot package System.Numerics.Tensors --version 10.0.11
dotnet add tests/DiscordGithubBot.Tests package NSubstitute --version 5.3.0
```
(Version-fallback rule from Global Constraints applies.)

- [ ] **Step 3: Ensure `<Nullable>enable</Nullable>` in both csproj files** (the templates usually add it; verify).

- [ ] **Step 4: Write `src/DiscordGithubBot/appsettings.json`** (defaults only, no secrets):

```json
{
  "Logging": { "LogLevel": { "Default": "Information", "Microsoft": "Warning" } },
  "Discord": { "Token": "" },
  "OpenAI": {
    "ApiKey": "",
    "ChatModel": "gpt-5.6-luna",
    "EmbeddingModel": "text-embedding-3-small"
  },
  "Database": { "Path": "db/app.db" },
  "Apps": []
}
```
Set it to copy to output: add to `src/DiscordGithubBot/DiscordGithubBot.csproj`:

```xml
<ItemGroup>
  <None Update="appsettings.json" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

- [ ] **Step 5: Write `.gitignore`**

```
bin/
obj/
db/
*.db
*.db-*
.env
secrets/
appsettings.*.local.json
.vs/
TestResults/
```

- [ ] **Step 6: Write `.env.example`** documenting every knob (values are examples):

```
# Discord bot token (Discord Developer Portal -> Bot -> Token)
Discord__Token=your-discord-bot-token

# OpenAI
OpenAI__ApiKey=sk-...
OpenAI__ChatModel=gpt-5.6-luna
OpenAI__EmbeddingModel=text-embedding-3-small

# SQLite location (inside container use /data/app.db)
Database__Path=db/app.db

# Apps: one block per configured app (index-based)
Apps__0__Name=MyApp
Apps__0__Repo=owner/repo
Apps__0__GitHubToken=github_pat_...
Apps__0__GuildIds__0=111111111111111111
Apps__0__ChannelIds__0=222222222222222222
```

- [ ] **Step 7: Write `APP.md`** — describe the app from the spec: what it does, the report workflow (modal → normalize → embed → top-5 cosine → LLM verdict → confirm → create/comment), the three slash commands, config reference (mirror `.env.example` + JSON shape + Docker secrets), how to run locally (`dotnet run`, `docker compose up`), and a pointer to `docs/superpowers/specs/2026-08-18-discord-github-issue-bot-design.md` and `docs/DECISIONS.md`. Write real prose, 60–120 lines.

- [ ] **Step 8: Write `CLAUDE.md`**:

```markdown
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
```

- [ ] **Step 9: Write `docs/DECISIONS.md`** — seed it with the 10 decisions from the spec's "Decisions log" section (copy them, one bullet each, dated 2026-08-18).

- [ ] **Step 10: Replace `SmokeTests.cs` default test** with:

```csharp
namespace DiscordGithubBot.Tests;

public class SmokeTests
{
    [Fact]
    public void ProjectCompiles() => Assert.True(true);
}
```
And delete the template's `UnitTest1.cs` / default `Program.cs` content: `src/DiscordGithubBot/Program.cs` becomes:

```csharp
Console.WriteLine("DiscordGithubBot placeholder — wired in the host task.");
```

- [ ] **Step 11: Verify build + tests**

Run: `dotnet build && dotnet test`
Expected: build clean, 1 test passes.

- [ ] **Step 12: Commit**

```bash
git add -A
git commit -m "chore: scaffold solution, pin packages, add APP.md/CLAUDE.md/DECISIONS.md and hygiene files"
```

---

### Task 2: Configuration options + validation

**Files:**
- Create: `src/DiscordGithubBot/Configuration/BotOptions.cs`
- Test: `tests/DiscordGithubBot.Tests/Configuration/BotOptionsTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces (used by nearly every later task):

```csharp
namespace DiscordGithubBot.Configuration;

public sealed class DiscordOptions { public string Token { get; set; } = ""; }

public sealed class OpenAIOptions
{
    public string ApiKey { get; set; } = "";
    public string ChatModel { get; set; } = "gpt-5.6-luna";
    public string EmbeddingModel { get; set; } = "text-embedding-3-small";
}

public sealed class DatabaseOptions { public string Path { get; set; } = "db/app.db"; }

public sealed class AppConfig
{
    public string Name { get; set; } = "";
    public string Repo { get; set; } = "";          // "owner/repo"
    public string GitHubToken { get; set; } = "";
    public List<ulong> GuildIds { get; set; } = new();
    public List<ulong> ChannelIds { get; set; } = new();
}

public sealed class BotOptions
{
    public DiscordOptions Discord { get; set; } = new();
    public OpenAIOptions OpenAI { get; set; } = new();
    public DatabaseOptions Database { get; set; } = new();
    public List<AppConfig> Apps { get; set; } = new();

    public IReadOnlyList<string> Validate();
    public IReadOnlyList<AppConfig> AppsForGuild(ulong guildId);
    public AppConfig? AppByRepo(string repo);
}
```

`Validate()` returns an empty list when valid, otherwise one human-readable message per problem, each naming the offending config key (e.g. `"Apps[1].Repo: must be 'owner/repo'"`).

- [ ] **Step 1: Write failing tests** `tests/DiscordGithubBot.Tests/Configuration/BotOptionsTests.cs`:

```csharp
using DiscordGithubBot.Configuration;
using Microsoft.Extensions.Configuration;

namespace DiscordGithubBot.Tests.Configuration;

public class BotOptionsTests
{
    private static BotOptions Valid() => new()
    {
        Discord = new() { Token = "t" },
        OpenAI = new() { ApiKey = "k" },
        Apps =
        [
            new AppConfig
            {
                Name = "MyApp", Repo = "owner/repo", GitHubToken = "pat",
                GuildIds = [1UL], ChannelIds = [2UL],
            },
        ],
    };

    [Fact]
    public void Valid_options_produce_no_errors() => Assert.Empty(Valid().Validate());

    [Fact]
    public void Missing_discord_token_is_reported()
    {
        var o = Valid(); o.Discord.Token = "";
        Assert.Contains(o.Validate(), e => e.Contains("Discord:Token"));
    }

    [Fact]
    public void Missing_openai_key_is_reported()
    {
        var o = Valid(); o.OpenAI.ApiKey = "";
        Assert.Contains(o.Validate(), e => e.Contains("OpenAI:ApiKey"));
    }

    [Fact]
    public void No_apps_is_reported()
    {
        var o = Valid(); o.Apps.Clear();
        Assert.Contains(o.Validate(), e => e.Contains("Apps"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("norepo")]
    [InlineData("owner/repo/extra")]
    public void Bad_repo_format_is_reported(string repo)
    {
        var o = Valid(); o.Apps[0].Repo = repo;
        Assert.Contains(o.Validate(), e => e.Contains("Repo"));
    }

    [Fact]
    public void Duplicate_repo_is_reported()
    {
        var o = Valid();
        o.Apps.Add(new AppConfig
        {
            Name = "Other", Repo = "owner/repo", GitHubToken = "pat2",
            GuildIds = [3UL], ChannelIds = [4UL],
        });
        Assert.Contains(o.Validate(), e => e.Contains("duplicate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void App_without_guilds_channels_token_or_name_is_reported()
    {
        var o = Valid();
        o.Apps[0].GuildIds.Clear(); o.Apps[0].ChannelIds.Clear();
        o.Apps[0].GitHubToken = ""; o.Apps[0].Name = "";
        var errors = o.Validate();
        Assert.Contains(errors, e => e.Contains("GuildIds"));
        Assert.Contains(errors, e => e.Contains("ChannelIds"));
        Assert.Contains(errors, e => e.Contains("GitHubToken"));
        Assert.Contains(errors, e => e.Contains("Name"));
    }

    [Fact]
    public void AppsForGuild_filters_by_guild()
    {
        var o = Valid();
        o.Apps.Add(new AppConfig
        {
            Name = "B", Repo = "owner/other", GitHubToken = "p",
            GuildIds = [9UL], ChannelIds = [2UL],
        });
        Assert.Single(o.AppsForGuild(1UL));
        Assert.Equal("owner/other", Assert.Single(o.AppsForGuild(9UL)).Repo);
        Assert.Empty(o.AppsForGuild(42UL));
    }

    [Fact]
    public void AppByRepo_finds_exact_repo()
    {
        Assert.NotNull(Valid().AppByRepo("owner/repo"));
        Assert.Null(Valid().AppByRepo("owner/none"));
    }

    [Fact]
    public void Binds_from_configuration_including_env_style_keys()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Discord:Token"] = "tok",
            ["OpenAI:ApiKey"] = "key",
            ["Apps:0:Name"] = "MyApp",
            ["Apps:0:Repo"] = "owner/repo",
            ["Apps:0:GitHubToken"] = "pat",
            ["Apps:0:GuildIds:0"] = "111111111111111111",
            ["Apps:0:ChannelIds:0"] = "222222222222222222",
        }).Build();
        var o = config.Get<BotOptions>()!;
        Assert.Empty(o.Validate());
        Assert.Equal(111111111111111111UL, o.Apps[0].GuildIds[0]);
        Assert.Equal("gpt-5.6-luna", o.OpenAI.ChatModel); // default survives binding
    }
}
```

- [ ] **Step 2: Run tests, verify they fail** (`dotnet test`): compile error, `BotOptions` not defined.

- [ ] **Step 3: Implement `src/DiscordGithubBot/Configuration/BotOptions.cs`** exactly per the Produces block. Implementation notes:

```csharp
public IReadOnlyList<string> Validate()
{
    var errors = new List<string>();
    if (string.IsNullOrWhiteSpace(Discord.Token)) errors.Add("Discord:Token is required.");
    if (string.IsNullOrWhiteSpace(OpenAI.ApiKey)) errors.Add("OpenAI:ApiKey is required.");
    if (string.IsNullOrWhiteSpace(OpenAI.ChatModel)) errors.Add("OpenAI:ChatModel is required.");
    if (string.IsNullOrWhiteSpace(OpenAI.EmbeddingModel)) errors.Add("OpenAI:EmbeddingModel is required.");
    if (string.IsNullOrWhiteSpace(Database.Path)) errors.Add("Database:Path is required.");
    if (Apps.Count == 0) errors.Add("Apps: at least one app must be configured.");
    for (var i = 0; i < Apps.Count; i++)
    {
        var app = Apps[i];
        var prefix = $"Apps[{i}]";
        if (string.IsNullOrWhiteSpace(app.Name)) errors.Add($"{prefix}.Name is required.");
        var parts = app.Repo.Split('/');
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
            errors.Add($"{prefix}.Repo: '{app.Repo}' must be 'owner/repo'.");
        if (string.IsNullOrWhiteSpace(app.GitHubToken)) errors.Add($"{prefix}.GitHubToken is required.");
        if (app.GuildIds.Count == 0) errors.Add($"{prefix}.GuildIds: at least one guild id is required.");
        if (app.ChannelIds.Count == 0) errors.Add($"{prefix}.ChannelIds: at least one channel id is required.");
    }
    var dupes = Apps.GroupBy(a => a.Repo, StringComparer.OrdinalIgnoreCase)
        .Where(g => !string.IsNullOrWhiteSpace(g.Key) && g.Count() > 1);
    errors.AddRange(dupes.Select(g => $"Apps: duplicate Repo '{g.Key}'."));
    return errors;
}

public IReadOnlyList<AppConfig> AppsForGuild(ulong guildId) =>
    Apps.Where(a => a.GuildIds.Contains(guildId)).ToList();

public AppConfig? AppByRepo(string repo) =>
    Apps.FirstOrDefault(a => string.Equals(a.Repo, repo, StringComparison.OrdinalIgnoreCase));
```

- [ ] **Step 4: Run tests, verify all pass** (`dotnet test`).

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: add BotOptions configuration model with fail-fast validation"
```

---

### Task 3: EF Core data model + vector BLOB converter

**Files:**
- Create: `src/DiscordGithubBot/Data/Entities.cs`, `src/DiscordGithubBot/Data/VectorConversion.cs`, `src/DiscordGithubBot/Data/BotDbContext.cs`
- Test: `tests/DiscordGithubBot.Tests/Data/BotDbContextTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:

```csharp
namespace DiscordGithubBot.Data;

public enum ReportType { Bug, Feature }

public class IssueEmbedding
{
    public int Id { get; set; }
    public required string RepoKey { get; set; }        // "owner/repo", lowercase
    public int IssueNumber { get; set; }
    public required string Title { get; set; }
    public required string State { get; set; }          // "open" | "closed"
    public DateTime? ClosedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public required string ContentHash { get; set; }    // SHA256 hex of title + "\n" + body
    public string BodyExcerpt { get; set; } = "";       // first 1000 chars of body
    public string HtmlUrl { get; set; } = "";
    public float[] Vector { get; set; } = [];
}

public class PendingReport
{
    public Guid Id { get; set; }
    public required string RepoKey { get; set; }
    public ulong DiscordUserId { get; set; }
    public required string ReporterDisplayName { get; set; }
    public ReportType Type { get; set; }
    public required string OriginalText { get; set; }
    public required string DraftTitle { get; set; }
    public required string DraftBody { get; set; }
    public string CandidatesJson { get; set; } = "[]";
    public DateTime CreatedAtUtc { get; set; }
    public List<PendingAttachment> Attachments { get; set; } = new();
}

public class PendingAttachment
{
    public int Id { get; set; }
    public Guid PendingReportId { get; set; }
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public required byte[] Bytes { get; set; }
}

public class RepoSyncState
{
    public required string RepoKey { get; set; }  // primary key
    public DateTime LastSyncUtc { get; set; }
}

public static class VectorConversion
{
    public static byte[] ToBytes(float[] vector);
    public static float[] FromBytes(byte[] bytes);
}

public class BotDbContext(DbContextOptions<BotDbContext> options) : DbContext(options)
{
    public DbSet<IssueEmbedding> IssueEmbeddings => Set<IssueEmbedding>();
    public DbSet<PendingReport> PendingReports => Set<PendingReport>();
    public DbSet<PendingAttachment> PendingAttachments => Set<PendingAttachment>();
    public DbSet<RepoSyncState> RepoSyncStates => Set<RepoSyncState>();
}
```

Model config: `IssueEmbedding` has unique index on (`RepoKey`,`IssueNumber`); `RepoSyncState` keyed by `RepoKey`; `PendingReport.Attachments` cascade-deletes; `Vector` uses a `ValueConverter<float[], byte[]>` built on `VectorConversion` **plus** a `ValueComparer<float[]>` (sequence equality) — the comparer is mandatory or change tracking misbehaves on mutable arrays. Schema is created with `EnsureCreated()` (no migrations — app owns its schema; decision recorded in Task 1's DECISIONS seed).

- [ ] **Step 1: Write failing tests** `tests/DiscordGithubBot.Tests/Data/BotDbContextTests.cs`:

```csharp
using DiscordGithubBot.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DiscordGithubBot.Tests.Data;

public sealed class BotDbContextTests : IDisposable
{
    private readonly SqliteConnection _conn;

    public BotDbContextTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
    }

    private BotDbContext NewContext()
    {
        var ctx = new BotDbContext(new DbContextOptionsBuilder<BotDbContext>()
            .UseSqlite(_conn).Options);
        ctx.Database.EnsureCreated();
        return ctx;
    }

    [Fact]
    public void Vector_conversion_round_trips()
    {
        float[] v = [1.5f, -2.25f, 0f, 3.75f];
        Assert.Equal(v, VectorConversion.FromBytes(VectorConversion.ToBytes(v)));
        Assert.Empty(VectorConversion.FromBytes(VectorConversion.ToBytes([])));
    }

    [Fact]
    public void IssueEmbedding_persists_vector_as_blob_and_round_trips()
    {
        using (var ctx = NewContext())
        {
            ctx.IssueEmbeddings.Add(new IssueEmbedding
            {
                RepoKey = "owner/repo", IssueNumber = 7, Title = "Crash on live",
                State = "open", UpdatedAtUtc = DateTime.UtcNow, ContentHash = "abc",
                Vector = [0.1f, 0.2f, 0.3f],
            });
            ctx.SaveChanges();
        }
        using (var ctx = NewContext())
        {
            var e = ctx.IssueEmbeddings.Single();
            Assert.Equal([0.1f, 0.2f, 0.3f], e.Vector);
        }
    }

    [Fact]
    public void Duplicate_repo_and_issue_number_violates_unique_index()
    {
        using var ctx = NewContext();
        ctx.IssueEmbeddings.AddRange(
            new IssueEmbedding { RepoKey = "o/r", IssueNumber = 1, Title = "a", State = "open", ContentHash = "h" },
            new IssueEmbedding { RepoKey = "o/r", IssueNumber = 1, Title = "b", State = "open", ContentHash = "h" });
        Assert.Throws<DbUpdateException>(() => ctx.SaveChanges());
    }

    [Fact]
    public void Deleting_pending_report_cascades_to_attachments()
    {
        var id = Guid.NewGuid();
        using (var ctx = NewContext())
        {
            ctx.PendingReports.Add(new PendingReport
            {
                Id = id, RepoKey = "o/r", DiscordUserId = 1, ReporterDisplayName = "u",
                Type = ReportType.Bug, OriginalText = "x", DraftTitle = "t", DraftBody = "b",
                CreatedAtUtc = DateTime.UtcNow,
                Attachments = [new PendingAttachment { FileName = "a.png", ContentType = "image/png", Bytes = [1, 2] }],
            });
            ctx.SaveChanges();
        }
        using (var ctx = NewContext())
        {
            ctx.PendingReports.Remove(ctx.PendingReports.Single(r => r.Id == id));
            ctx.SaveChanges();
            Assert.Empty(ctx.PendingAttachments.ToList());
        }
    }
}
```

- [ ] **Step 2: Run tests, verify they fail to compile** (types missing).

- [ ] **Step 3: Implement.** `VectorConversion` via `System.Runtime.InteropServices.MemoryMarshal`:

```csharp
public static byte[] ToBytes(float[] vector) => MemoryMarshal.AsBytes<float>(vector).ToArray();
public static float[] FromBytes(byte[] bytes) => MemoryMarshal.Cast<byte, float>(bytes).ToArray();
```

`BotDbContext.OnModelCreating`:

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    var issue = modelBuilder.Entity<IssueEmbedding>();
    issue.HasIndex(e => new { e.RepoKey, e.IssueNumber }).IsUnique();
    issue.Property(e => e.Vector)
        .HasConversion(v => VectorConversion.ToBytes(v), b => VectorConversion.FromBytes(b))
        .Metadata.SetValueComparer(new ValueComparer<float[]>(
            (a, b) => (a ?? Array.Empty<float>()).SequenceEqual(b ?? Array.Empty<float>()),
            v => v.Aggregate(17, (h, f) => HashCode.Combine(h, f)),
            v => v.ToArray()));

    modelBuilder.Entity<RepoSyncState>().HasKey(s => s.RepoKey);

    modelBuilder.Entity<PendingReport>()
        .HasMany(r => r.Attachments)
        .WithOne()
        .HasForeignKey(a => a.PendingReportId)
        .OnDelete(DeleteBehavior.Cascade);
}
```
(`ValueComparer` is in `Microsoft.EntityFrameworkCore.ChangeTracking`.)

- [ ] **Step 4: Run tests, verify all pass.**

- [ ] **Step 5: Commit** — `feat: add EF Core model with float[]<->BLOB vector conversion`

---

### Task 4: Cosine similarity ranking

**Files:**
- Create: `src/DiscordGithubBot/Pipeline/VectorRanker.cs`
- Test: `tests/DiscordGithubBot.Tests/Pipeline/VectorRankerTests.cs`

**Interfaces:**
- Consumes: `IssueEmbedding` (Task 3).
- Produces:

```csharp
namespace DiscordGithubBot.Pipeline;

public sealed record RankedIssue(IssueEmbedding Issue, float Score);

public static class VectorRanker
{
    public const int EmbeddingDimensions = 1536; // the single source of truth
    public static IReadOnlyList<RankedIssue> TopK(
        ReadOnlyMemory<float> query, IEnumerable<IssueEmbedding> candidates, int k);
}
```

Behavior: cosine similarity via `System.Numerics.Tensors.TensorPrimitives.CosineSimilarity(ReadOnlySpan<float>, ReadOnlySpan<float>)`; candidates whose `Vector.Length` differs from `query.Length` (or is 0) are skipped; results sorted descending by score, at most `k` items.

- [ ] **Step 1: Write failing tests** `tests/DiscordGithubBot.Tests/Pipeline/VectorRankerTests.cs`:

```csharp
using DiscordGithubBot.Data;
using DiscordGithubBot.Pipeline;

namespace DiscordGithubBot.Tests.Pipeline;

public class VectorRankerTests
{
    private static IssueEmbedding Issue(int number, params float[] v) => new()
    {
        RepoKey = "o/r", IssueNumber = number, Title = $"#{number}", State = "open",
        ContentHash = "h", Vector = v,
    };

    [Fact]
    public void Ranks_by_cosine_similarity_descending()
    {
        float[] query = [1f, 0f, 0f];
        var ranked = VectorRanker.TopK(query,
            [Issue(1, 0f, 1f, 0f), Issue(2, 1f, 0f, 0f), Issue(3, 0.9f, 0.1f, 0f)], 5);
        Assert.Equal([2, 3, 1], ranked.Select(r => r.Issue.IssueNumber).ToArray());
        Assert.Equal(1f, ranked[0].Score, 3);
    }

    [Fact]
    public void Returns_at_most_k()
    {
        float[] query = [1f, 0f];
        var ranked = VectorRanker.TopK(query,
            Enumerable.Range(1, 10).Select(i => Issue(i, 1f, i / 10f)), 5);
        Assert.Equal(5, ranked.Count);
    }

    [Fact]
    public void Skips_dimension_mismatches_and_empty_vectors()
    {
        float[] query = [1f, 0f];
        var ranked = VectorRanker.TopK(query,
            [Issue(1, 1f, 0f, 0f), Issue(2), Issue(3, 1f, 0f)], 5);
        Assert.Equal(3, Assert.Single(ranked).Issue.IssueNumber);
    }

    [Fact]
    public void Empty_candidates_gives_empty_result() =>
        Assert.Empty(VectorRanker.TopK(new float[] { 1f }, [], 5));
}
```

- [ ] **Step 2: Run tests, verify compile failure.**

- [ ] **Step 3: Implement:**

```csharp
public static IReadOnlyList<RankedIssue> TopK(
    ReadOnlyMemory<float> query, IEnumerable<IssueEmbedding> candidates, int k)
{
    var results = new List<RankedIssue>();
    foreach (var c in candidates)
    {
        if (c.Vector.Length == 0 || c.Vector.Length != query.Length) continue;
        var score = TensorPrimitives.CosineSimilarity(query.Span, c.Vector);
        results.Add(new RankedIssue(c, score));
    }
    return results.OrderByDescending(r => r.Score).Take(k).ToList();
}
```

- [ ] **Step 4: Run tests, verify all pass.**

- [ ] **Step 5: Commit** — `feat: add in-memory cosine top-k ranking over issue embeddings`

---

### Task 5: GitHub issues service

**Files:**
- Create: `src/DiscordGithubBot/GitHub/GitHubService.cs` (includes `GitHubIssue` record + `IGitHubService`)
- Test: `tests/DiscordGithubBot.Tests/GitHub/GitHubServiceTests.cs`, `tests/DiscordGithubBot.Tests/TestDoubles/FakeHttpMessageHandler.cs`

**Interfaces:**
- Consumes: `AppConfig` (Task 2).
- Produces:

```csharp
namespace DiscordGithubBot.GitHub;

public sealed record GitHubIssue(
    int Number, string Title, string Body, string State,
    DateTime UpdatedAtUtc, DateTime? ClosedAtUtc, string HtmlUrl);

public interface IGitHubService
{
    Task<GitHubIssue> CreateIssueAsync(AppConfig app, string title, string body, string label, CancellationToken ct = default);
    /// <returns>The html_url of the created comment.</returns>
    Task<string> AddCommentAsync(AppConfig app, int issueNumber, string body, CancellationToken ct = default);
    /// <param name="state">"open" | "closed" | "all"</param>
    /// <param name="sinceUtc">maps to the GitHub 'since' query param (updated-at filter) when set</param>
    Task<IReadOnlyList<GitHubIssue>> ListIssuesAsync(AppConfig app, string state, DateTime? sinceUtc, CancellationToken ct = default);
}

public sealed class GitHubService(HttpClient http) : IGitHubService { ... }
```

Implementation contract:
- `http.BaseAddress` is `https://api.github.com/`; `User-Agent: discord-gh-issue-bot`, `X-GitHub-Api-Version: 2022-11-28`, and `Accept: application/vnd.github+json` headers are set by DI (Task 12); tests set BaseAddress on the test client. Every request adds `Authorization: Bearer {app.GitHubToken}` per-request (PATs differ per app).
- `ListIssuesAsync` paginates `GET repos/{repo}/issues?state={state}&per_page=100&page={n}` (plus `&since={sinceUtc:yyyy-MM-ddTHH:mm:ssZ}` when set) until a page returns fewer than 100 items; items containing a `pull_request` property are **skipped** (GitHub returns PRs from the issues endpoint).
- `CreateIssueAsync` sends `POST repos/{repo}/issues` with `{"title","body","labels":[label]}`.
- `AddCommentAsync` sends `POST repos/{repo}/issues/{n}/comments` with `{"body"}`.
- JSON via `System.Text.Json` (`JsonSerializer` + private DTOs with `[JsonPropertyName]`, or `JsonDocument`) — implementer's choice.
- Non-success status throws `HttpRequestException` (use `EnsureSuccessStatusCode`); callers handle.

- [ ] **Step 1: Write the shared fake handler** `tests/DiscordGithubBot.Tests/TestDoubles/FakeHttpMessageHandler.cs` (used by Tasks 5, 6, 9):

```csharp
using System.Net;

namespace DiscordGithubBot.Tests.TestDoubles;

/// <summary>Scripted HTTP handler: routes match in registration order, records requests.</summary>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    public sealed record Recorded(HttpMethod Method, string Url, string? Body, string? AuthHeader);

    private readonly List<(Func<HttpRequestMessage, bool> Match, Func<HttpRequestMessage, HttpResponseMessage> Respond)> _routes = new();
    public List<Recorded> Requests { get; } = new();

    public void When(HttpMethod method, string urlContains, HttpStatusCode status, string jsonBody) =>
        _routes.Add((
            req => req.Method == method && req.RequestUri!.ToString().Contains(urlContains),
            _ => new HttpResponseMessage(status)
            {
                Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json"),
            }));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Requests.Add(new Recorded(request.Method, request.RequestUri!.ToString(), body,
            request.Headers.Authorization?.ToString()));
        var route = _routes.FirstOrDefault(r => r.Match(request));
        return route.Respond is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") }
            : route.Respond(request);
    }

    public HttpClient CreateClient() => new(this) { BaseAddress = new Uri("https://api.github.com/") };
}
```

- [ ] **Step 2: Write failing tests** `tests/DiscordGithubBot.Tests/GitHub/GitHubServiceTests.cs`:

```csharp
using System.Net;
using DiscordGithubBot.Configuration;
using DiscordGithubBot.GitHub;
using DiscordGithubBot.Tests.TestDoubles;

namespace DiscordGithubBot.Tests.GitHub;

public class GitHubServiceTests
{
    private static readonly AppConfig App = new()
    {
        Name = "MyApp", Repo = "owner/repo", GitHubToken = "PAT123",
        GuildIds = [1UL], ChannelIds = [2UL],
    };

    [Fact]
    public async Task CreateIssue_posts_title_body_label_and_bearer_token()
    {
        var fake = new FakeHttpMessageHandler();
        fake.When(HttpMethod.Post, "repos/owner/repo/issues", HttpStatusCode.Created,
            """{"number":42,"title":"T","body":"B","state":"open","updated_at":"2026-08-18T00:00:00Z","closed_at":null,"html_url":"https://github.com/owner/repo/issues/42"}""");
        var svc = new GitHubService(fake.CreateClient());

        var issue = await svc.CreateIssueAsync(App, "T", "B", "bug");

        Assert.Equal(42, issue.Number);
        Assert.Equal("https://github.com/owner/repo/issues/42", issue.HtmlUrl);
        var req = Assert.Single(fake.Requests);
        Assert.Equal("Bearer PAT123", req.AuthHeader);
        Assert.Contains("\"bug\"", req.Body);
        Assert.Contains("\"T\"", req.Body);
    }

    [Fact]
    public async Task AddComment_returns_comment_url()
    {
        var fake = new FakeHttpMessageHandler();
        fake.When(HttpMethod.Post, "repos/owner/repo/issues/7/comments", HttpStatusCode.Created,
            """{"html_url":"https://github.com/owner/repo/issues/7#issuecomment-1"}""");
        var svc = new GitHubService(fake.CreateClient());

        var url = await svc.AddCommentAsync(App, 7, "hello");

        Assert.Equal("https://github.com/owner/repo/issues/7#issuecomment-1", url);
    }

    [Fact]
    public async Task ListIssues_filters_pull_requests_and_maps_fields()
    {
        var fake = new FakeHttpMessageHandler();
        fake.When(HttpMethod.Get, "repos/owner/repo/issues?", HttpStatusCode.OK,
            """
            [
              {"number":1,"title":"Bug A","body":"b","state":"open","updated_at":"2026-08-01T10:00:00Z","closed_at":null,"html_url":"u1"},
              {"number":2,"title":"PR","body":"p","state":"open","updated_at":"2026-08-01T10:00:00Z","closed_at":null,"html_url":"u2","pull_request":{"url":"x"}},
              {"number":3,"title":"Bug B","body":null,"state":"closed","updated_at":"2026-08-02T10:00:00Z","closed_at":"2026-08-02T10:00:00Z","html_url":"u3"}
            ]
            """);
        var svc = new GitHubService(fake.CreateClient());

        var issues = await svc.ListIssuesAsync(App, "all", null);

        Assert.Equal([1, 3], issues.Select(i => i.Number).ToArray());
        Assert.Equal("", issues[1].Body);           // null body -> empty string
        Assert.NotNull(issues[1].ClosedAtUtc);
        Assert.Equal(DateTimeKind.Utc, issues[0].UpdatedAtUtc.Kind);
    }

    [Fact]
    public async Task ListIssues_passes_state_and_since()
    {
        var fake = new FakeHttpMessageHandler();
        fake.When(HttpMethod.Get, "repos/owner/repo/issues?", HttpStatusCode.OK, "[]");
        var svc = new GitHubService(fake.CreateClient());

        await svc.ListIssuesAsync(App, "all", new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));

        var url = Assert.Single(fake.Requests).Url;
        Assert.Contains("state=all", url);
        Assert.Contains("since=2026-08-01T00%3A00%3A00Z", url);
        Assert.Contains("per_page=100", url);
    }

    [Fact]
    public async Task Failure_status_throws()
    {
        var fake = new FakeHttpMessageHandler();
        fake.When(HttpMethod.Post, "repos/owner/repo/issues", HttpStatusCode.Unauthorized, "{}");
        var svc = new GitHubService(fake.CreateClient());
        await Assert.ThrowsAsync<HttpRequestException>(() => svc.CreateIssueAsync(App, "t", "b", "bug"));
    }
}
```

Note on pagination: a page with fewer than 100 items ends the loop, so the single-page fakes above terminate naturally.

- [ ] **Step 3: Run tests, verify compile failure.**

- [ ] **Step 4: Implement `GitHubService`.** Core request helper sketch:

```csharp
private async Task<HttpResponseMessage> SendAsync(AppConfig app, HttpMethod method, string path, object? payload, CancellationToken ct)
{
    using var req = new HttpRequestMessage(method, path);
    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", app.GitHubToken);
    if (payload is not null)
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
    var resp = await http.SendAsync(req, ct);
    resp.EnsureSuccessStatusCode();
    return resp;
}
```
Issue DTO: private class with `[JsonPropertyName("number")]` etc.; `pull_request` mapped as `JsonElement?` — skip the item when present. `since` formatted with `Uri.EscapeDataString(sinceUtc.Value.ToString("yyyy-MM-ddTHH:mm:ssZ"))`. Parse dates so `Kind == Utc` (`DateTimeStyles.AdjustToUniversal` or `DateTime.SpecifyKind`).

- [ ] **Step 5: Run tests, verify all pass.**

- [ ] **Step 6: Commit** — `feat: add GitHub issues service (create/comment/list with PR filtering and pagination)`

---

### Task 6: Two-tier image uploader

**Files:**
- Create: `src/DiscordGithubBot/GitHub/ImageUploader.cs` (includes `UploadedImage`, `IImageUploader`, `GitHubImageUploader`)
- Test: `tests/DiscordGithubBot.Tests/GitHub/ImageUploaderTests.cs`

**Interfaces:**
- Consumes: `AppConfig` (Task 2), `FakeHttpMessageHandler` (Task 5).
- Produces:

```csharp
namespace DiscordGithubBot.GitHub;

public sealed record UploadedImage(string FileName, string Url);

public interface IImageUploader
{
    /// <returns>null when every strategy failed — callers must treat this as "note the failure, continue".</returns>
    Task<UploadedImage?> UploadAsync(AppConfig app, string fileName, string contentType, byte[] bytes, CancellationToken ct = default);
}

public sealed class GitHubImageUploader(HttpClient http, ILogger<GitHubImageUploader> logger) : IImageUploader { ... }
```

Behavior (two-tier, per spec):
1. **Primary — unofficial user-attachments endpoint.**
   `GET repos/{repo}` gives `id` (repository id, cached per repo in a `ConcurrentDictionary<string, long>`), then
   `POST https://uploads.github.com/user-attachments/assets?name={UrlEncoded fileName}&repository_id={id}` (absolute URL — ignores BaseAddress) with `Authorization: Bearer`, `Content-Type: {contentType}`, raw bytes body.
   On 2xx: parse the response JSON leniently — take the first string property whose value contains `user-attachments/assets` (check `href`, `url`, `asset_url`, then any string property of the root object). Return `UploadedImage(fileName, thatUrl)`.
   On ANY failure (non-2xx, exception, unparseable body): log a warning and fall through to tier 2.
2. **Fallback — Contents API on an `issue-assets` branch.**
   - `GET repos/{repo}/branches/issue-assets` — if 404: `GET repos/{repo}` for `default_branch`, `GET repos/{repo}/git/ref/heads/{default_branch}` for `object.sha`, `POST repos/{repo}/git/refs` with `{"ref":"refs/heads/issue-assets","sha":sha}` (spec said orphan branch; branch-from-default-HEAD is a simplification — record it in DECISIONS.md when implementing).
   - `PUT repos/{repo}/contents/issue-assets/{yyyyMMddHHmmssfff}-{sanitized fileName}` with `{"message":"chore: add issue screenshot","content":"<base64>","branch":"issue-assets"}`.
   - Return `UploadedImage(fileName, $"https://raw.githubusercontent.com/{repo}/issue-assets/{path}")`.
3. Both tiers failed: log error, return `null`.

- [ ] **Step 1: Write failing tests** `tests/DiscordGithubBot.Tests/GitHub/ImageUploaderTests.cs`:

```csharp
using System.Net;
using DiscordGithubBot.Configuration;
using DiscordGithubBot.GitHub;
using DiscordGithubBot.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiscordGithubBot.Tests.GitHub;

public class ImageUploaderTests
{
    private static readonly AppConfig App = new()
    {
        Name = "MyApp", Repo = "owner/repo", GitHubToken = "PAT",
        GuildIds = [1UL], ChannelIds = [2UL],
    };

    private static GitHubImageUploader Uploader(FakeHttpMessageHandler fake) =>
        new(fake.CreateClient(), NullLogger<GitHubImageUploader>.Instance);

    [Fact]
    public async Task Uses_unofficial_endpoint_when_it_works()
    {
        var fake = new FakeHttpMessageHandler();
        fake.When(HttpMethod.Post, "uploads.github.com/user-attachments/assets", HttpStatusCode.OK,
            """{"id":"x","href":"https://github.com/user-attachments/assets/abc-123"}""");
        fake.When(HttpMethod.Get, "repos/owner/repo", HttpStatusCode.OK, """{"id":1296269,"default_branch":"main"}""");

        var result = await Uploader(fake).UploadAsync(App, "shot.png", "image/png", [1, 2, 3]);

        Assert.Equal("https://github.com/user-attachments/assets/abc-123", result!.Url);
        Assert.Contains(fake.Requests, r => r.Url.Contains("repository_id=1296269"));
    }

    [Fact]
    public async Task Falls_back_to_contents_api_when_unofficial_endpoint_fails()
    {
        var fake = new FakeHttpMessageHandler();
        fake.When(HttpMethod.Post, "uploads.github.com", HttpStatusCode.NotFound, "{}");
        fake.When(HttpMethod.Get, "repos/owner/repo/branches/issue-assets", HttpStatusCode.OK, """{"name":"issue-assets"}""");
        fake.When(HttpMethod.Put, "repos/owner/repo/contents/issue-assets/", HttpStatusCode.Created,
            """{"content":{"path":"issue-assets/x.png"}}""");
        fake.When(HttpMethod.Get, "repos/owner/repo", HttpStatusCode.OK, """{"id":1296269,"default_branch":"main"}""");

        var result = await Uploader(fake).UploadAsync(App, "shot.png", "image/png", [1, 2, 3]);

        Assert.NotNull(result);
        Assert.StartsWith("https://raw.githubusercontent.com/owner/repo/issue-assets/", result.Url);
        Assert.EndsWith("-shot.png", result.Url);
        var put = fake.Requests.Single(r => r.Method == HttpMethod.Put);
        Assert.Contains("issue-assets", put.Body);
    }

    [Fact]
    public async Task Creates_assets_branch_when_missing()
    {
        var fake = new FakeHttpMessageHandler();
        fake.When(HttpMethod.Post, "uploads.github.com", HttpStatusCode.Unauthorized, "{}");
        fake.When(HttpMethod.Get, "repos/owner/repo/branches/issue-assets", HttpStatusCode.NotFound, "{}");
        fake.When(HttpMethod.Get, "repos/owner/repo/git/ref/heads/main", HttpStatusCode.OK,
            """{"object":{"sha":"abc123"}}""");
        fake.When(HttpMethod.Post, "repos/owner/repo/git/refs", HttpStatusCode.Created, "{}");
        fake.When(HttpMethod.Put, "repos/owner/repo/contents/issue-assets/", HttpStatusCode.Created, "{}");
        fake.When(HttpMethod.Get, "repos/owner/repo", HttpStatusCode.OK, """{"id":1,"default_branch":"main"}""");

        var result = await Uploader(fake).UploadAsync(App, "shot.png", "image/png", [1]);

        Assert.NotNull(result);
        var refPost = fake.Requests.Single(r => r.Method == HttpMethod.Post && r.Url.Contains("git/refs"));
        Assert.Contains("refs/heads/issue-assets", refPost.Body);
        Assert.Contains("abc123", refPost.Body);
    }

    [Fact]
    public async Task Returns_null_when_everything_fails()
    {
        var fake = new FakeHttpMessageHandler(); // no routes: everything 404s
        var result = await Uploader(fake).UploadAsync(App, "shot.png", "image/png", [1]);
        Assert.Null(result);
    }
}
```

Route-ordering caveat: `GET repos/owner/repo` also matches longer URLs, so tests register the more specific routes first and the fake matches in registration order.

- [ ] **Step 2: Run tests, verify compile failure.**

- [ ] **Step 3: Implement `GitHubImageUploader`** per the behavior contract. Notes:
- Sanitize file names for the contents path: keep letters/digits/`.`/`-`/`_`, replace the rest with `_`.
- Unique path: `DateTime.UtcNow.ToString("yyyyMMddHHmmssfff")` + `-` + sanitized name (avoids needing the existing-file SHA).
- Wrap tier 1 and tier 2 each in try/catch; `logger.LogWarning(ex, ...)` on tier-1 failure, `logger.LogError(ex, ...)` when both fail.

- [ ] **Step 4: Run tests, verify all pass.**

- [ ] **Step 5: Commit** — `feat: add two-tier GitHub image uploader (user-attachments endpoint with contents-API fallback)`

---

### Task 7: AI report normalizer + duplicate judge

**Files:**
- Create: `src/DiscordGithubBot/Ai/ReportNormalizer.cs`, `src/DiscordGithubBot/Ai/DuplicateJudge.cs`
- Test: `tests/DiscordGithubBot.Tests/Ai/ReportNormalizerTests.cs`, `tests/DiscordGithubBot.Tests/Ai/DuplicateJudgeTests.cs`, `tests/DiscordGithubBot.Tests/TestDoubles/FakeChatClient.cs`

**Interfaces:**
- Consumes: `ReportType`, `IssueEmbedding` (Task 3); `IChatClient` (Microsoft.Extensions.AI).
- Produces:

```csharp
namespace DiscordGithubBot.Ai;

public sealed record IssueDraft(string Title, string Body);

public sealed class NormalizationException(string message, Exception? inner = null)
    : Exception(message, inner);

public interface IReportNormalizer
{
    /// <summary>Turns raw user text into a clean issue draft. Throws NormalizationException after one retry.</summary>
    Task<IssueDraft> NormalizeAsync(ReportType type, string appName, string rawText, CancellationToken ct = default);
}

public enum VerdictKind { Match, Uncertain, NoMatch }

/// <param name="IssueNumber">set when Kind == Match</param>
/// <param name="CandidateNumbers">issue numbers worth showing when Kind == Uncertain (subset of input candidates)</param>
public sealed record DuplicateVerdict(VerdictKind Kind, int? IssueNumber, IReadOnlyList<int> CandidateNumbers);

public interface IDuplicateJudge
{
    /// <summary>Empty candidates short-circuits to NoMatch without an LLM call. LLM/parse failure degrades to Uncertain over all candidates.</summary>
    Task<DuplicateVerdict> JudgeAsync(IssueDraft draft, IReadOnlyList<IssueEmbedding> candidates, CancellationToken ct = default);
}

public sealed class ReportNormalizer(IChatClient chat, ILogger<ReportNormalizer> logger) : IReportNormalizer { ... }
public sealed class DuplicateJudge(IChatClient chat, ILogger<DuplicateJudge> logger) : IDuplicateJudge { ... }
```

Implementation contract:
- Both services use the Microsoft.Extensions.AI structured-output extension: `await chat.GetResponseAsync<TDto>(prompt, cancellationToken: ct)` returning `ChatResponse<TDto>`; **always** use `response.TryGetResult(out var dto)` — never `.Result` (throws on malformed output).
- **Normalizer prompt** (single user message, string interpolation): states the app name; instructs: rewrite the report as a well-formed GitHub issue in English; for `ReportType.Bug` use sections `## Description`, `## Steps to Reproduce`, `## Expected Behavior`, `## Actual Behavior` (omit a section rather than inventing facts); for `ReportType.Feature` use `## Summary`, `## Motivation`, `## Proposed Solution`; title ≤ 80 chars, imperative, no trailing period; never invent details not present in the report. DTO: `sealed class IssueDraftDto { public string Title { get; set; } = ""; public string Body { get; set; } = ""; }`. `TryGetResult` false or blank title → retry once; second failure → throw `NormalizationException`.
- **Judge prompt**: presents the draft (title + body) and each candidate as `#{number} [{state}] {title}\n{bodyExcerpt}`; asks whether the new report describes the same underlying issue as exactly one candidate (`match`), possibly one of several (`uncertain`), or none (`no_match`); instructs: "Only answer match when you are confident it is the same defect/request, not merely the same feature area." DTO: `sealed class VerdictDto { public string Verdict { get; set; } = "no_match"; public int? IssueNumber { get; set; } public int[]? Candidates { get; set; } }`. Mapping rules:
  - `verdict == "match"` but `IssueNumber` not among the input candidate numbers → degrade to `Uncertain` over all input candidates (defensive).
  - `verdict == "uncertain"` → `CandidateNumbers` = intersection of `Candidates` with input numbers; if empty, all input numbers.
  - anything unparseable / exception → log warning, `Uncertain` over all input numbers.
  - `candidates.Count == 0` → `NoMatch` immediately, no LLM call.

- [ ] **Step 1: Write the fake chat client** `tests/DiscordGithubBot.Tests/TestDoubles/FakeChatClient.cs`:

```csharp
using Microsoft.Extensions.AI;

namespace DiscordGithubBot.Tests.TestDoubles;

/// <summary>Returns scripted assistant texts in sequence; records prompts. Works with GetResponseAsync&lt;T&gt; because the structured-output layer parses assistant text as JSON.</summary>
public sealed class FakeChatClient(params string[] responses) : IChatClient
{
    private int _call;
    public List<string> Prompts { get; } = new();

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Prompts.Add(string.Join("\n", messages.Select(m => m.Text)));
        var text = responses[Math.Min(_call++, responses.Length - 1)];
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
```
(If `ChatResponse`/`GetResponseAsync` signatures differ in Microsoft.Extensions.AI 10.9.0, adapt the fake to the real `IChatClient` interface — the production code must keep using `GetResponseAsync<T>` + `TryGetResult`.)

- [ ] **Step 2: Write failing normalizer tests** `tests/DiscordGithubBot.Tests/Ai/ReportNormalizerTests.cs`:

```csharp
using DiscordGithubBot.Ai;
using DiscordGithubBot.Data;
using DiscordGithubBot.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiscordGithubBot.Tests.Ai;

public class ReportNormalizerTests
{
    [Fact]
    public async Task Returns_draft_from_valid_llm_json()
    {
        var chat = new FakeChatClient("""{"title":"Crash when clicking Live","body":"## Description\nCrash."}""");
        var sut = new ReportNormalizer(chat, NullLogger<ReportNormalizer>.Instance);

        var draft = await sut.NormalizeAsync(ReportType.Bug, "MyApp", "app crashed on live button");

        Assert.Equal("Crash when clicking Live", draft.Title);
        Assert.Contains("## Description", draft.Body);
        Assert.Contains("app crashed on live button", chat.Prompts[0]); // raw text reaches the prompt
        Assert.Contains("MyApp", chat.Prompts[0]);
    }

    [Fact]
    public async Task Retries_once_then_succeeds()
    {
        var chat = new FakeChatClient("not json at all", """{"title":"T","body":"B"}""");
        var sut = new ReportNormalizer(chat, NullLogger<ReportNormalizer>.Instance);
        var draft = await sut.NormalizeAsync(ReportType.Feature, "MyApp", "raw");
        Assert.Equal("T", draft.Title);
        Assert.Equal(2, chat.Prompts.Count);
    }

    [Fact]
    public async Task Throws_after_two_failures()
    {
        var chat = new FakeChatClient("garbage");
        var sut = new ReportNormalizer(chat, NullLogger<ReportNormalizer>.Instance);
        await Assert.ThrowsAsync<NormalizationException>(
            () => sut.NormalizeAsync(ReportType.Bug, "MyApp", "raw"));
    }

    [Fact]
    public async Task Bug_and_feature_prompts_differ()
    {
        var chat = new FakeChatClient("""{"title":"T","body":"B"}""");
        var sut = new ReportNormalizer(chat, NullLogger<ReportNormalizer>.Instance);
        await sut.NormalizeAsync(ReportType.Bug, "MyApp", "x");
        Assert.Contains("Steps to Reproduce", chat.Prompts[0]);
        await sut.NormalizeAsync(ReportType.Feature, "MyApp", "x");
        Assert.Contains("Motivation", chat.Prompts[1]);
    }
}
```

- [ ] **Step 3: Write failing judge tests** `tests/DiscordGithubBot.Tests/Ai/DuplicateJudgeTests.cs`:

```csharp
using DiscordGithubBot.Ai;
using DiscordGithubBot.Data;
using DiscordGithubBot.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiscordGithubBot.Tests.Ai;

public class DuplicateJudgeTests
{
    private static IssueEmbedding Candidate(int n, string state = "open") => new()
    {
        RepoKey = "o/r", IssueNumber = n, Title = $"Issue {n}", State = state,
        ContentHash = "h", BodyExcerpt = $"body {n}",
    };

    private static DuplicateJudge Sut(FakeChatClient chat) => new(chat, NullLogger<DuplicateJudge>.Instance);
    private static readonly IssueDraft Draft = new("T", "B");

    [Fact]
    public async Task Match_verdict_maps_to_match()
    {
        var chat = new FakeChatClient("""{"verdict":"match","issueNumber":7}""");
        var v = await Sut(chat).JudgeAsync(Draft, [Candidate(7), Candidate(9)]);
        Assert.Equal(VerdictKind.Match, v.Kind);
        Assert.Equal(7, v.IssueNumber);
    }

    [Fact]
    public async Task Match_with_unknown_issue_number_degrades_to_uncertain()
    {
        var chat = new FakeChatClient("""{"verdict":"match","issueNumber":999}""");
        var v = await Sut(chat).JudgeAsync(Draft, [Candidate(7), Candidate(9)]);
        Assert.Equal(VerdictKind.Uncertain, v.Kind);
        Assert.Equal([7, 9], v.CandidateNumbers);
    }

    [Fact]
    public async Task Uncertain_intersects_candidates()
    {
        var chat = new FakeChatClient("""{"verdict":"uncertain","candidates":[9,999]}""");
        var v = await Sut(chat).JudgeAsync(Draft, [Candidate(7), Candidate(9)]);
        Assert.Equal(VerdictKind.Uncertain, v.Kind);
        Assert.Equal([9], v.CandidateNumbers);
    }

    [Fact]
    public async Task No_match_maps_to_nomatch()
    {
        var chat = new FakeChatClient("""{"verdict":"no_match"}""");
        var v = await Sut(chat).JudgeAsync(Draft, [Candidate(7)]);
        Assert.Equal(VerdictKind.NoMatch, v.Kind);
    }

    [Fact]
    public async Task Garbage_degrades_to_uncertain_over_all()
    {
        var chat = new FakeChatClient("garbage");
        var v = await Sut(chat).JudgeAsync(Draft, [Candidate(7), Candidate(9)]);
        Assert.Equal(VerdictKind.Uncertain, v.Kind);
        Assert.Equal([7, 9], v.CandidateNumbers);
    }

    [Fact]
    public async Task Empty_candidates_short_circuits_without_llm_call()
    {
        var chat = new FakeChatClient("should never be used");
        var v = await Sut(chat).JudgeAsync(Draft, []);
        Assert.Equal(VerdictKind.NoMatch, v.Kind);
        Assert.Empty(chat.Prompts);
    }

    [Fact]
    public async Task Candidate_body_excerpts_reach_the_prompt()
    {
        var chat = new FakeChatClient("""{"verdict":"no_match"}""");
        await Sut(chat).JudgeAsync(Draft, [Candidate(7)]);
        Assert.Contains("body 7", chat.Prompts[0]);
        Assert.Contains("#7", chat.Prompts[0]);
    }
}
```

- [ ] **Step 4: Run tests, verify compile failure.**

- [ ] **Step 5: Implement `ReportNormalizer` and `DuplicateJudge`** per the contract. Truncate each candidate's `BodyExcerpt` to 1000 chars and the raw report to 4000 chars inside prompts.

- [ ] **Step 6: Run tests, verify all pass.**

- [ ] **Step 7: Commit** — `feat: add AI report normalizer and duplicate judge with graceful degradation`

---

### Task 8: Issue body composer

**Files:**
- Create: `src/DiscordGithubBot/Pipeline/IssueBodyComposer.cs`
- Test: `tests/DiscordGithubBot.Tests/Pipeline/IssueBodyComposerTests.cs`

**Interfaces:**
- Consumes: `UploadedImage` (Task 6).
- Produces:

```csharp
namespace DiscordGithubBot.Pipeline;

public static class IssueBodyComposer
{
    public static string ComposeIssueBody(
        string draftBody, string reporterDisplayName,
        IReadOnlyList<UploadedImage> images, IReadOnlyList<string> failedUploads,
        int? regressionOfIssueNumber);

    public static string ComposeCommentBody(
        string draftBody, string reporterDisplayName,
        IReadOnlyList<UploadedImage> images, IReadOnlyList<string> failedUploads);
}
```

Output layout (issue body; comment body is identical minus the regression line):

```
{draftBody}

Possible regression of #{n}.            <- only when regressionOfIssueNumber set

### Screenshots                          <- only when images non-empty
![{FileName}]({Url})                     <- one line per image

> [!NOTE]                                <- only when failedUploads non-empty
> Screenshot upload failed for: {names, comma-joined}.

---
_Reported by **{reporterDisplayName}** via Discord._
```

- [ ] **Step 1: Write failing tests** `tests/DiscordGithubBot.Tests/Pipeline/IssueBodyComposerTests.cs`:

```csharp
using DiscordGithubBot.GitHub;
using DiscordGithubBot.Pipeline;

namespace DiscordGithubBot.Tests.Pipeline;

public class IssueBodyComposerTests
{
    [Fact]
    public void Minimal_body_has_reporter_footer_only()
    {
        var body = IssueBodyComposer.ComposeIssueBody("The body.", "markus", [], [], null);
        Assert.StartsWith("The body.", body);
        Assert.Contains("_Reported by **markus** via Discord._", body);
        Assert.DoesNotContain("Screenshots", body);
        Assert.DoesNotContain("regression", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("upload failed", body);
    }

    [Fact]
    public void Images_render_as_markdown_gallery()
    {
        var body = IssueBodyComposer.ComposeIssueBody("B", "u",
            [new UploadedImage("a.png", "https://x/a"), new UploadedImage("b.png", "https://x/b")], [], null);
        Assert.Contains("### Screenshots", body);
        Assert.Contains("![a.png](https://x/a)", body);
        Assert.Contains("![b.png](https://x/b)", body);
    }

    [Fact]
    public void Regression_reference_is_included()
    {
        var body = IssueBodyComposer.ComposeIssueBody("B", "u", [], [], 42);
        Assert.Contains("Possible regression of #42.", body);
    }

    [Fact]
    public void Failed_uploads_are_noted()
    {
        var body = IssueBodyComposer.ComposeIssueBody("B", "u", [], ["x.png", "y.png"], null);
        Assert.Contains("x.png, y.png", body);
        Assert.Contains("upload failed", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Comment_body_never_has_regression_line()
    {
        var body = IssueBodyComposer.ComposeCommentBody("B", "u",
            [new UploadedImage("a.png", "https://x/a")], []);
        Assert.Contains("![a.png](https://x/a)", body);
        Assert.Contains("_Reported by **u** via Discord._", body);
        Assert.DoesNotContain("regression", body, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 2: Run tests, verify compile failure.**

- [ ] **Step 3: Implement** with a `StringBuilder`; both public methods share one private core.

- [ ] **Step 4: Run tests, verify all pass.**

- [ ] **Step 5: Commit** — `feat: add issue/comment body composer with screenshots, regression ref and reporter credit`

---

### Task 9: Issue embedding sync service

**Files:**
- Create: `src/DiscordGithubBot/Pipeline/IssueSyncService.cs`
- Test: `tests/DiscordGithubBot.Tests/Pipeline/IssueSyncServiceTests.cs`, `tests/DiscordGithubBot.Tests/TestDoubles/FakeEmbeddingGenerator.cs`

**Interfaces:**
- Consumes: `BotDbContext`, `IssueEmbedding`, `RepoSyncState` (Task 3); `IGitHubService`, `GitHubIssue` (Task 5); `IEmbeddingGenerator<string, Embedding<float>>` (Microsoft.Extensions.AI); `AppConfig` (Task 2).
- Produces:

```csharp
namespace DiscordGithubBot.Pipeline;

public interface IIssueSyncService
{
    /// <summary>Incrementally refreshes the embedding cache for the app's repo. Never throws on GitHub/embedding failure — logs and leaves the cache stale.</summary>
    Task SyncAsync(AppConfig app, CancellationToken ct = default);

    /// <summary>Candidates for dedup: open issues plus issues closed within the last 30 days. Prunes older closed rows.</summary>
    Task<IReadOnlyList<IssueEmbedding>> GetCandidatesAsync(string repoKey, CancellationToken ct = default);
}

public sealed class IssueSyncService(
    BotDbContext db, IGitHubService gitHub,
    IEmbeddingGenerator<string, Embedding<float>> embedder,
    ILogger<IssueSyncService> logger) : IIssueSyncService { ... }
```

Behavior contract for `SyncAsync`:
1. `syncStartUtc = DateTime.UtcNow` captured **before** the GitHub call (so nothing updated mid-sync is missed next time).
2. `since` = existing `RepoSyncState.LastSyncUtc` or `null` (first sync fetches everything).
3. `gitHub.ListIssuesAsync(app, "all", since)`; for each issue: compute `hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(issue.Title + "\n" + issue.Body)))`; upsert the `IssueEmbedding` row (match on RepoKey + IssueNumber); always refresh `Title/State/ClosedAtUtc/UpdatedAtUtc/HtmlUrl/BodyExcerpt` (first 1000 chars of body); re-embed **only** when the hash changed or the row is new — embed text is `issue.Title + "\n\n" + issue.Body` truncated to 8000 chars, via `embedder.GenerateVectorAsync(text, cancellationToken: ct)` (returns `ReadOnlyMemory<float>`; store `.ToArray()`).
4. Save `RepoSyncState.LastSyncUtc = syncStartUtc`, `SaveChangesAsync`.
5. Any exception: `logger.LogWarning(ex, ...)`, swallow (stale cache is better than a dead report flow — spec's resilience decision).

`GetCandidatesAsync`: delete rows where `State == "closed" && ClosedAtUtc < now-30d`, save, then return remaining rows for `repoKey`.

- [ ] **Step 1: Write the fake embedder** `tests/DiscordGithubBot.Tests/TestDoubles/FakeEmbeddingGenerator.cs`:

```csharp
using Microsoft.Extensions.AI;

namespace DiscordGithubBot.Tests.TestDoubles;

/// <summary>Deterministic embedder: vector = f(text hash); records inputs.</summary>
public sealed class FakeEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public List<string> Inputs { get; } = new();

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = new List<Embedding<float>>();
        foreach (var v in values)
        {
            Inputs.Add(v);
            var seed = (float)(Math.Abs(v.GetHashCode()) % 1000) / 1000f;
            list.Add(new Embedding<float>(new float[] { seed, 1f - seed, 0.5f }));
        }
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(list));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
```

- [ ] **Step 2: Write failing tests** `tests/DiscordGithubBot.Tests/Pipeline/IssueSyncServiceTests.cs` (NSubstitute for `IGitHubService`; in-memory SQLite context per the Task 3 test pattern):

```csharp
using DiscordGithubBot.Configuration;
using DiscordGithubBot.Data;
using DiscordGithubBot.GitHub;
using DiscordGithubBot.Pipeline;
using DiscordGithubBot.Tests.TestDoubles;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace DiscordGithubBot.Tests.Pipeline;

public sealed class IssueSyncServiceTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly BotDbContext _db;
    private readonly IGitHubService _gitHub = Substitute.For<IGitHubService>();
    private readonly FakeEmbeddingGenerator _embedder = new();
    private readonly IssueSyncService _sut;

    private static readonly AppConfig App = new()
    {
        Name = "MyApp", Repo = "owner/repo", GitHubToken = "p",
        GuildIds = [1UL], ChannelIds = [2UL],
    };

    public IssueSyncServiceTests()
    {
        _conn.Open();
        _db = new BotDbContext(new DbContextOptionsBuilder<BotDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
        _sut = new IssueSyncService(_db, _gitHub, _embedder, NullLogger<IssueSyncService>.Instance);
    }

    private static GitHubIssue Issue(int n, string title = "t", string body = "b", string state = "open",
        DateTime? closedAt = null) =>
        new(n, title, body, state, DateTime.UtcNow, closedAt, $"https://github.com/owner/repo/issues/{n}");

    [Fact]
    public async Task First_sync_embeds_all_issues_and_records_sync_state()
    {
        _gitHub.ListIssuesAsync(App, "all", null, Arg.Any<CancellationToken>())
            .Returns([Issue(1), Issue(2)]);

        await _sut.SyncAsync(App);

        Assert.Equal(2, _db.IssueEmbeddings.Count());
        Assert.All(_db.IssueEmbeddings, e => Assert.NotEmpty(e.Vector));
        Assert.NotNull(_db.RepoSyncStates.Find("owner/repo"));
        Assert.Equal(2, _embedder.Inputs.Count);
    }

    [Fact]
    public async Task Second_sync_passes_since_and_skips_reembedding_unchanged_content()
    {
        _gitHub.ListIssuesAsync(App, "all", null, Arg.Any<CancellationToken>()).Returns([Issue(1)]);
        await _sut.SyncAsync(App);

        _gitHub.ListIssuesAsync(App, "all", Arg.Is<DateTime?>(d => d != null), Arg.Any<CancellationToken>())
            .Returns([Issue(1)]); // same title+body -> same hash
        await _sut.SyncAsync(App);

        Assert.Equal(1, _db.IssueEmbeddings.Count());
        Assert.Single(_embedder.Inputs); // no second embedding call
    }

    [Fact]
    public async Task Changed_content_is_reembedded_and_state_updated()
    {
        _gitHub.ListIssuesAsync(App, "all", null, Arg.Any<CancellationToken>()).Returns([Issue(1)]);
        await _sut.SyncAsync(App);

        _gitHub.ListIssuesAsync(App, "all", Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns([Issue(1, title: "new title", state: "closed", closedAt: DateTime.UtcNow)]);
        await _sut.SyncAsync(App);

        var e = _db.IssueEmbeddings.Single();
        Assert.Equal("new title", e.Title);
        Assert.Equal("closed", e.State);
        Assert.Equal(2, _embedder.Inputs.Count);
    }

    [Fact]
    public async Task GitHub_failure_is_swallowed_and_cache_left_intact()
    {
        _gitHub.ListIssuesAsync(App, "all", null, Arg.Any<CancellationToken>()).Returns([Issue(1)]);
        await _sut.SyncAsync(App);

        _gitHub.ListIssuesAsync(App, "all", Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<GitHubIssue>>>(_ => throw new HttpRequestException("down"));
        await _sut.SyncAsync(App); // must not throw

        Assert.Equal(1, _db.IssueEmbeddings.Count());
    }

    [Fact]
    public async Task Candidates_include_open_and_recently_closed_but_prune_old_closed()
    {
        _db.IssueEmbeddings.AddRange(
            new IssueEmbedding { RepoKey = "owner/repo", IssueNumber = 1, Title = "open", State = "open", ContentHash = "h", Vector = [1f] },
            new IssueEmbedding { RepoKey = "owner/repo", IssueNumber = 2, Title = "recent", State = "closed", ClosedAtUtc = DateTime.UtcNow.AddDays(-5), ContentHash = "h", Vector = [1f] },
            new IssueEmbedding { RepoKey = "owner/repo", IssueNumber = 3, Title = "old", State = "closed", ClosedAtUtc = DateTime.UtcNow.AddDays(-45), ContentHash = "h", Vector = [1f] },
            new IssueEmbedding { RepoKey = "other/repo", IssueNumber = 4, Title = "foreign", State = "open", ContentHash = "h", Vector = [1f] });
        await _db.SaveChangesAsync();

        var candidates = await _sut.GetCandidatesAsync("owner/repo");

        Assert.Equal([1, 2], candidates.Select(c => c.IssueNumber).Order().ToArray());
        Assert.Null(await _db.IssueEmbeddings.SingleOrDefaultAsync(e => e.IssueNumber == 3)); // pruned
    }

    public void Dispose() { _db.Dispose(); _conn.Dispose(); }
}
```

- [ ] **Step 3: Run tests, verify compile failure.**

- [ ] **Step 4: Implement `IssueSyncService`** per the behavior contract. `GenerateVectorAsync` is the extension method on `IEmbeddingGenerator<string, Embedding<float>>` (namespace `Microsoft.Extensions.AI`).

- [ ] **Step 5: Run tests, verify all pass.**

- [ ] **Step 6: Commit** — `feat: add incremental issue embedding sync with hash-based re-embed and 30-day candidate window`

---

### Task 10: Pending report store + report pipeline

**Files:**
- Create: `src/DiscordGithubBot/Pipeline/PendingReportStore.cs`, `src/DiscordGithubBot/Pipeline/ReportPipeline.cs` (includes the pipeline records)
- Test: `tests/DiscordGithubBot.Tests/Pipeline/PendingReportStoreTests.cs`, `tests/DiscordGithubBot.Tests/Pipeline/ReportPipelineTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 2–9.
- Produces:

```csharp
namespace DiscordGithubBot.Pipeline;

public sealed record AttachmentPayload(string FileName, string ContentType, byte[] Bytes);

public sealed record ReportSubmission(
    AppConfig App, ReportType Type, ulong DiscordUserId,
    string ReporterDisplayName, string RawText,
    IReadOnlyList<AttachmentPayload> Attachments);

/// <summary>A dedup candidate as shown to the user; serialized into PendingReport.CandidatesJson.</summary>
public sealed record CandidateIssue(int Number, string Title, string State, string Url);

public enum ReportOutcomeKind { MatchOpen, MatchClosed, Uncertain, NoMatch }

/// <param name="Match">set for MatchOpen/MatchClosed</param>
/// <param name="Candidates">set for Uncertain (1..5 items); empty otherwise</param>
public sealed record ReportOutcome(
    ReportOutcomeKind Kind, Guid PendingReportId, IssueDraft Draft,
    CandidateIssue? Match, IReadOnlyList<CandidateIssue> Candidates);

public sealed record CreatedIssueResult(int Number, string Title, string HtmlUrl);
public sealed record CommentResult(int IssueNumber, string CommentUrl);

public sealed class ExpiredPendingReportException() : Exception("This report session has expired — please submit again.");

public interface IPendingReportStore
{
    Task SaveAsync(PendingReport report, CancellationToken ct = default);
    /// <returns>null when unknown OR older than 1 hour (expired rows are deleted on read).</returns>
    Task<PendingReport?> GetAsync(Guid id, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    /// <summary>Deletes all reports older than 1 hour. Called by the maintenance service.</summary>
    Task<int> CleanupExpiredAsync(CancellationToken ct = default);
}

public interface IReportPipeline
{
    /// <summary>Modal submit -> normalized draft -> dedup verdict. Persists a PendingReport and returns the routed outcome.</summary>
    Task<ReportOutcome> ProcessAsync(ReportSubmission submission, CancellationToken ct = default);

    /// <summary>Confirm-create: uploads images, creates the GitHub issue, deletes the pending report.</summary>
    /// <exception cref="ExpiredPendingReportException"/>
    Task<CreatedIssueResult> CreateIssueAsync(Guid pendingReportId, int? regressionOfIssueNumber, CancellationToken ct = default);

    /// <summary>Confirm-duplicate: uploads images, comments on the existing issue, deletes the pending report.</summary>
    /// <exception cref="ExpiredPendingReportException"/>
    Task<CommentResult> AddCommentAsync(Guid pendingReportId, int issueNumber, CancellationToken ct = default);

    /// <summary>Cancel: drops the pending report if it still exists.</summary>
    Task CancelAsync(Guid pendingReportId, CancellationToken ct = default);

    /// <summary>Non-destructive read of pending state (draft, candidates, repo) for component handlers; null when unknown or expired.</summary>
    Task<PendingReport?> PeekAsync(Guid pendingReportId, CancellationToken ct = default);
}

public sealed class PendingReportStore(BotDbContext db) : IPendingReportStore { ... }

public sealed class ReportPipeline(
    IReportNormalizer normalizer,
    IEmbeddingGenerator<string, Embedding<float>> embedder,
    IIssueSyncService sync,
    IDuplicateJudge judge,
    IPendingReportStore store,
    IGitHubService gitHub,
    IImageUploader imageUploader,
    BotOptions options,
    ILogger<ReportPipeline> logger) : IReportPipeline { ... }
```

`ProcessAsync` contract:
1. `draft = await normalizer.NormalizeAsync(submission.Type, submission.App.Name, submission.RawText, ct)` (NormalizationException propagates — Discord layer turns it into an ephemeral error).
2. `queryVector = await embedder.GenerateVectorAsync(draft.Title + "\n\n" + draft.Body, ct)`.
3. `await sync.SyncAsync(submission.App, ct)` (never throws), then `candidates = await sync.GetCandidatesAsync(submission.App.Repo, ct)`.
4. `ranked = VectorRanker.TopK(queryVector, candidates, 5)`.
5. `verdict = await judge.JudgeAsync(draft, ranked.Select(r => r.Issue).ToList(), ct)`.
6. Build `CandidateIssue` list from ranked issues (`Number=IssueNumber, Title, State, Url=HtmlUrl`). Persist a `PendingReport` (new `Guid`, `CandidatesJson = JsonSerializer.Serialize(candidateIssues)`, attachments copied from submission, `CreatedAtUtc = DateTime.UtcNow`).
7. Route:
   - `verdict.Kind == Match`: find the matched `CandidateIssue`; its `State == "open"` → `MatchOpen`, else `MatchClosed` (Match set, Candidates empty).
   - `Uncertain` → `Uncertain` with the candidates whose numbers are in `verdict.CandidateNumbers`.
   - `NoMatch` → `NoMatch` (Match null, Candidates empty).

`CreateIssueAsync(id, regressionOf)` contract: `store.GetAsync` → null throws `ExpiredPendingReportException`; resolve `app = options.AppByRepo(report.RepoKey)` (null → `InvalidOperationException`); upload each attachment via `imageUploader.UploadAsync` collecting successes + failed file names; `body = IssueBodyComposer.ComposeIssueBody(report.DraftBody, report.ReporterDisplayName, images, failures, regressionOf)`; `label = report.Type == ReportType.Bug ? "bug" : "enhancement"`; `gitHub.CreateIssueAsync(app, report.DraftTitle, body, label, ct)`; `store.DeleteAsync`; return `CreatedIssueResult`.

`AddCommentAsync(id, issueNumber)` contract: same load/expire/app-resolve logic; uploads; `body = IssueBodyComposer.ComposeCommentBody(...)`; `gitHub.AddCommentAsync(app, issueNumber, body, ct)`; delete pending; return `CommentResult(issueNumber, commentUrl)`.

- [ ] **Step 1: Write failing store tests** `tests/DiscordGithubBot.Tests/Pipeline/PendingReportStoreTests.cs` (in-memory SQLite per Task 3 pattern):

```csharp
using DiscordGithubBot.Data;
using DiscordGithubBot.Pipeline;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DiscordGithubBot.Tests.Pipeline;

public sealed class PendingReportStoreTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly BotDbContext _db;
    private readonly PendingReportStore _sut;

    public PendingReportStoreTests()
    {
        _conn.Open();
        _db = new BotDbContext(new DbContextOptionsBuilder<BotDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
        _sut = new PendingReportStore(_db);
    }

    private static PendingReport Report(Guid id, DateTime createdUtc) => new()
    {
        Id = id, RepoKey = "o/r", DiscordUserId = 1, ReporterDisplayName = "u",
        Type = ReportType.Bug, OriginalText = "x", DraftTitle = "t", DraftBody = "b",
        CreatedAtUtc = createdUtc,
        Attachments = [new PendingAttachment { FileName = "a.png", ContentType = "image/png", Bytes = [1] }],
    };

    [Fact]
    public async Task Save_get_round_trips_with_attachments()
    {
        var id = Guid.NewGuid();
        await _sut.SaveAsync(Report(id, DateTime.UtcNow));
        var loaded = await _sut.GetAsync(id);
        Assert.NotNull(loaded);
        Assert.Single(loaded.Attachments);
    }

    [Fact]
    public async Task Get_expired_returns_null_and_deletes()
    {
        var id = Guid.NewGuid();
        await _sut.SaveAsync(Report(id, DateTime.UtcNow.AddHours(-2)));
        Assert.Null(await _sut.GetAsync(id));
        Assert.Empty(_db.PendingReports.ToList());
    }

    [Fact]
    public async Task Get_unknown_returns_null() => Assert.Null(await _sut.GetAsync(Guid.NewGuid()));

    [Fact]
    public async Task Cleanup_deletes_only_expired()
    {
        await _sut.SaveAsync(Report(Guid.NewGuid(), DateTime.UtcNow.AddHours(-2)));
        await _sut.SaveAsync(Report(Guid.NewGuid(), DateTime.UtcNow));
        var removed = await _sut.CleanupExpiredAsync();
        Assert.Equal(1, removed);
        Assert.Equal(1, _db.PendingReports.Count());
    }

    public void Dispose() { _db.Dispose(); _conn.Dispose(); }
}
```

- [ ] **Step 2: Write failing pipeline tests** `tests/DiscordGithubBot.Tests/Pipeline/ReportPipelineTests.cs`. Use NSubstitute for `IReportNormalizer`, `IIssueSyncService`, `IDuplicateJudge`, `IGitHubService`, `IImageUploader`, `IPendingReportStore`; use `FakeEmbeddingGenerator` (Task 9). Shared arrange helper:

```csharp
using DiscordGithubBot.Ai;
using DiscordGithubBot.Configuration;
using DiscordGithubBot.Data;
using DiscordGithubBot.GitHub;
using DiscordGithubBot.Pipeline;
using DiscordGithubBot.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace DiscordGithubBot.Tests.Pipeline;

public class ReportPipelineTests
{
    private readonly IReportNormalizer _normalizer = Substitute.For<IReportNormalizer>();
    private readonly IIssueSyncService _sync = Substitute.For<IIssueSyncService>();
    private readonly IDuplicateJudge _judge = Substitute.For<IDuplicateJudge>();
    private readonly IPendingReportStore _store = Substitute.For<IPendingReportStore>();
    private readonly IGitHubService _gitHub = Substitute.For<IGitHubService>();
    private readonly IImageUploader _uploader = Substitute.For<IImageUploader>();
    private readonly BotOptions _options;
    private readonly ReportPipeline _sut;

    private static readonly AppConfig App = new()
    {
        Name = "MyApp", Repo = "owner/repo", GitHubToken = "p",
        GuildIds = [1UL], ChannelIds = [2UL],
    };

    public ReportPipelineTests()
    {
        _options = new BotOptions { Apps = [App] };
        _sut = new ReportPipeline(_normalizer, new FakeEmbeddingGenerator(), _sync, _judge,
            _store, _gitHub, _uploader, _options, NullLogger<ReportPipeline>.Instance);
        _normalizer.NormalizeAsync(Arg.Any<ReportType>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new IssueDraft("Draft title", "Draft body"));
    }

    private static ReportSubmission Submission(params AttachmentPayload[] attachments) =>
        new(App, ReportType.Bug, 42UL, "markus", "it broke", attachments);

    private static IssueEmbedding Candidate(int n, string state = "open", DateTime? closedUtc = null) => new()
    {
        RepoKey = "owner/repo", IssueNumber = n, Title = $"Issue {n}", State = state,
        ClosedAtUtc = closedUtc, ContentHash = "h", HtmlUrl = $"https://github.com/owner/repo/issues/{n}",
        Vector = [0.5f, 0.5f, 0.5f],
    };

    private void SetupCandidates(params IssueEmbedding[] candidates) =>
        _sync.GetCandidatesAsync("owner/repo", Arg.Any<CancellationToken>())
            .Returns(candidates.ToList());

    private void SetupVerdict(DuplicateVerdict verdict) =>
        _judge.JudgeAsync(Arg.Any<IssueDraft>(), Arg.Any<IReadOnlyList<IssueEmbedding>>(), Arg.Any<CancellationToken>())
            .Returns(verdict);

    [Fact]
    public async Task No_match_routes_to_preview()
    {
        SetupCandidates(Candidate(1));
        SetupVerdict(new DuplicateVerdict(VerdictKind.NoMatch, null, []));

        var outcome = await _sut.ProcessAsync(Submission());

        Assert.Equal(ReportOutcomeKind.NoMatch, outcome.Kind);
        Assert.Equal("Draft title", outcome.Draft.Title);
        Assert.NotEqual(Guid.Empty, outcome.PendingReportId);
        await _store.Received(1).SaveAsync(Arg.Is<PendingReport>(r =>
            r.DraftTitle == "Draft title" && r.RepoKey == "owner/repo"), Arg.Any<CancellationToken>());
        await _gitHub.DidNotReceiveWithAnyArgs().CreateIssueAsync(default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task Match_on_open_issue_routes_to_match_open()
    {
        SetupCandidates(Candidate(7));
        SetupVerdict(new DuplicateVerdict(VerdictKind.Match, 7, []));

        var outcome = await _sut.ProcessAsync(Submission());

        Assert.Equal(ReportOutcomeKind.MatchOpen, outcome.Kind);
        Assert.Equal(7, outcome.Match!.Number);
    }

    [Fact]
    public async Task Match_on_recently_closed_issue_routes_to_match_closed()
    {
        SetupCandidates(Candidate(7, "closed", DateTime.UtcNow.AddDays(-3)));
        SetupVerdict(new DuplicateVerdict(VerdictKind.Match, 7, []));

        var outcome = await _sut.ProcessAsync(Submission());

        Assert.Equal(ReportOutcomeKind.MatchClosed, outcome.Kind);
        Assert.Equal(7, outcome.Match!.Number);
    }

    [Fact]
    public async Task Uncertain_routes_with_filtered_candidates()
    {
        SetupCandidates(Candidate(7), Candidate(9), Candidate(11));
        SetupVerdict(new DuplicateVerdict(VerdictKind.Uncertain, null, [9, 11]));

        var outcome = await _sut.ProcessAsync(Submission());

        Assert.Equal(ReportOutcomeKind.Uncertain, outcome.Kind);
        Assert.Equal([9, 11], outcome.Candidates.Select(c => c.Number).ToArray());
    }

    [Fact]
    public async Task Sync_runs_before_candidates_are_read()
    {
        SetupCandidates();
        SetupVerdict(new DuplicateVerdict(VerdictKind.NoMatch, null, []));
        await _sut.ProcessAsync(Submission());
        Received.InOrder(() =>
        {
            _sync.SyncAsync(App, Arg.Any<CancellationToken>());
            _sync.GetCandidatesAsync("owner/repo", Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task CreateIssue_uploads_images_composes_body_and_deletes_pending()
    {
        var id = Guid.NewGuid();
        _store.GetAsync(id, Arg.Any<CancellationToken>()).Returns(new PendingReport
        {
            Id = id, RepoKey = "owner/repo", DiscordUserId = 42, ReporterDisplayName = "markus",
            Type = ReportType.Bug, OriginalText = "x", DraftTitle = "T", DraftBody = "B",
            CreatedAtUtc = DateTime.UtcNow,
            Attachments =
            [
                new PendingAttachment { FileName = "ok.png", ContentType = "image/png", Bytes = [1] },
                new PendingAttachment { FileName = "bad.png", ContentType = "image/png", Bytes = [2] },
            ],
        });
        _uploader.UploadAsync(App, "ok.png", "image/png", Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(new UploadedImage("ok.png", "https://gh/ok"));
        _uploader.UploadAsync(App, "bad.png", "image/png", Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns((UploadedImage?)null);
        _gitHub.CreateIssueAsync(App, "T", Arg.Any<string>(), "bug", Arg.Any<CancellationToken>())
            .Returns(new GitHubIssue(101, "T", "B", "open", DateTime.UtcNow, null, "https://gh/101"));

        var result = await _sut.CreateIssueAsync(id, regressionOfIssueNumber: 7);

        Assert.Equal(101, result.Number);
        Assert.Equal("https://gh/101", result.HtmlUrl);
        await _gitHub.Received(1).CreateIssueAsync(App, "T",
            Arg.Is<string>(b => b.Contains("https://gh/ok") && b.Contains("bad.png")
                && b.Contains("Possible regression of #7.") && b.Contains("markus")),
            "bug", Arg.Any<CancellationToken>());
        await _store.Received(1).DeleteAsync(id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateIssue_with_expired_pending_throws()
    {
        _store.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((PendingReport?)null);
        await Assert.ThrowsAsync<ExpiredPendingReportException>(() => _sut.CreateIssueAsync(Guid.NewGuid(), null));
    }

    [Fact]
    public async Task AddComment_composes_comment_and_deletes_pending()
    {
        var id = Guid.NewGuid();
        _store.GetAsync(id, Arg.Any<CancellationToken>()).Returns(new PendingReport
        {
            Id = id, RepoKey = "owner/repo", DiscordUserId = 42, ReporterDisplayName = "markus",
            Type = ReportType.Bug, OriginalText = "x", DraftTitle = "T", DraftBody = "B",
            CreatedAtUtc = DateTime.UtcNow,
        });
        _gitHub.AddCommentAsync(App, 7, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("https://gh/7#c1");

        var result = await _sut.AddCommentAsync(id, 7);

        Assert.Equal("https://gh/7#c1", result.CommentUrl);
        await _gitHub.Received(1).AddCommentAsync(App, 7,
            Arg.Is<string>(b => b.Contains("markus") && b.Contains("B")), Arg.Any<CancellationToken>());
        await _store.Received(1).DeleteAsync(id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Feature_reports_use_enhancement_label()
    {
        var id = Guid.NewGuid();
        _store.GetAsync(id, Arg.Any<CancellationToken>()).Returns(new PendingReport
        {
            Id = id, RepoKey = "owner/repo", DiscordUserId = 1, ReporterDisplayName = "u",
            Type = ReportType.Feature, OriginalText = "x", DraftTitle = "T", DraftBody = "B",
            CreatedAtUtc = DateTime.UtcNow,
        });
        _gitHub.CreateIssueAsync(App, "T", Arg.Any<string>(), "enhancement", Arg.Any<CancellationToken>())
            .Returns(new GitHubIssue(5, "T", "B", "open", DateTime.UtcNow, null, "u"));

        await _sut.CreateIssueAsync(id, null);

        await _gitHub.Received(1).CreateIssueAsync(App, "T", Arg.Any<string>(), "enhancement", Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 3: Run tests, verify compile failure.**

- [ ] **Step 4: Implement `PendingReportStore`** (include attachments via `.Include(r => r.Attachments)`; expiry cutoff `DateTime.UtcNow.AddHours(-1)`), **then `ReportPipeline`** per the contracts above (`CancelAsync` = `store.DeleteAsync` passthrough; `PeekAsync` = `store.GetAsync` passthrough).

- [ ] **Step 5: Run tests, verify all pass** (store + pipeline + all previous suites).

- [ ] **Step 6: Commit** — `feat: add report pipeline with pending-state store and verdict routing`

---

### Task 11: Discord layer — commands, modal, component handlers, renderers

**Files:**
- Create: `src/DiscordGithubBot/Discord/CustomIds.cs`, `src/DiscordGithubBot/Discord/ReportModal.cs`, `src/DiscordGithubBot/Discord/ReportInteractionModule.cs`, `src/DiscordGithubBot/Discord/OutcomeRenderer.cs`, `src/DiscordGithubBot/Discord/AttachmentDownloader.cs`, `src/DiscordGithubBot/Discord/BotService.cs`
- Test: `tests/DiscordGithubBot.Tests/Discord/CustomIdsTests.cs`

**Interfaces:**
- Consumes: `IReportPipeline` + records (Task 10), `IGitHubService` (Task 5), `BotOptions`/`AppConfig` (Task 2), `ReportType` (Task 3).
- Produces: `BotService` (registered as hosted service in Task 12), `CustomIds` codec, `AttachmentDownloader`.

**External-API note:** This task talks to Discord.Net 3.20.1. The shapes below reflect its documented API; if a builder/attribute name differs at compile time, consult https://docs.discordnet.dev for the 3.20 name and record any rename in `docs/DECISIONS.md`. Do NOT downgrade the approach (no reverting to CV1-only messages, no dropping the modal file upload).

**Spec deviation (record in DECISIONS.md):** the spec sketched the app selector as a string-select inside the modal; it is implemented instead as an optional `app` option on the slash command (simpler and more reliable — the modal custom id already carries the chosen repo, and the modal keeps a single description + file-upload layout).

**Custom-id scheme** (all interaction routing goes through this):
- Modal: `report-modal|{bug|feature}|{owner/repo}`
- Components: `rep|{action}|{pendingReportGuid}|{issueNumber}` where `action` ∈ `create` (create issue; issueNumber = regression-of, `0` = none), `cancel`, `comment` (comment on issueNumber), `draft` (show draft preview), `stillopen` (closed-match → user says still happening; issueNumber = the closed issue), `fixed` (closed-match → user accepts fix; issueNumber = the closed issue), `pick` (uncertain-list select menu; chosen issue number arrives as the select **value**, issueNumber segment is `0`).

```csharp
namespace DiscordGithubBot.Discord;

public static class CustomIds
{
    public const string Prefix = "rep";
    public const string Create = "create";
    public const string Cancel = "cancel";
    public const string Comment = "comment";
    public const string Draft = "draft";
    public const string StillOpen = "stillopen";
    public const string Fixed = "fixed";
    public const string Pick = "pick";

    public static string Build(string action, Guid id, int issueNumber = 0) =>
        $"{Prefix}|{action}|{id:N}|{issueNumber}";

    public static bool TryParse(string customId, out string action, out Guid id, out int issueNumber);
}
```

- [ ] **Step 1: Write failing codec tests** `tests/DiscordGithubBot.Tests/Discord/CustomIdsTests.cs`:

```csharp
using DiscordGithubBot.Discord;

namespace DiscordGithubBot.Tests.Discord;

public class CustomIdsTests
{
    [Fact]
    public void Round_trips_action_guid_and_issue_number()
    {
        var id = Guid.NewGuid();
        var s = CustomIds.Build(CustomIds.Comment, id, 42);
        Assert.True(CustomIds.TryParse(s, out var action, out var parsedId, out var n));
        Assert.Equal("comment", action);
        Assert.Equal(id, parsedId);
        Assert.Equal(42, n);
    }

    [Fact]
    public void Default_issue_number_is_zero()
    {
        Assert.True(CustomIds.TryParse(CustomIds.Build(CustomIds.Cancel, Guid.NewGuid()), out _, out _, out var n));
        Assert.Equal(0, n);
    }

    [Theory]
    [InlineData("")]
    [InlineData("rep|create")]                 // too few segments
    [InlineData("other|create|00000000000000000000000000000000|0")]
    [InlineData("rep|create|not-a-guid|0")]
    [InlineData("rep|create|00000000000000000000000000000000|NaN")]
    public void Rejects_malformed_ids(string s) => Assert.False(CustomIds.TryParse(s, out _, out _, out _));

    [Fact]
    public void Stays_within_discord_100_char_limit() =>
        Assert.InRange(CustomIds.Build(CustomIds.StillOpen, Guid.NewGuid(), int.MaxValue).Length, 1, 100);
}
```

- [ ] **Step 2: Run codec tests, verify compile failure; implement `CustomIds`; verify pass.**

- [ ] **Step 3: Implement `AttachmentDownloader`** — downloads modal attachments before slow work (Discord CDN URLs expire ~24h) and enforces limits:

```csharp
namespace DiscordGithubBot.Discord;

/// <summary>Downloads Discord attachments into memory. Skips non-images and files over 10 MB; returns (payloads, skippedNames).</summary>
public sealed class AttachmentDownloader(HttpClient http, ILogger<AttachmentDownloader> logger)
{
    public const long MaxBytes = 10 * 1024 * 1024;

    public async Task<(IReadOnlyList<AttachmentPayload> Payloads, IReadOnlyList<string> Skipped)>
        DownloadAsync(IEnumerable<IAttachment> attachments, CancellationToken ct = default)
    {
        var payloads = new List<AttachmentPayload>();
        var skipped = new List<string>();
        foreach (var a in attachments)
        {
            if (a.ContentType?.StartsWith("image/") != true || a.Size > MaxBytes) { skipped.Add(a.Filename); continue; }
            try
            {
                var bytes = await http.GetByteArrayAsync(a.Url, ct);
                payloads.Add(new AttachmentPayload(a.Filename, a.ContentType, bytes));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to download attachment {Name}", a.Filename);
                skipped.Add(a.Filename);
            }
        }
        return (payloads, skipped);
    }
}
```

- [ ] **Step 4: Implement `ReportModal`** (typed modal, Discord.Net interaction framework):

```csharp
namespace DiscordGithubBot.Discord;

public class ReportModal : IModal
{
    public string Title => "Report";

    [InputLabel("What happened? Include steps if you can.")]
    [ModalTextInput("description", TextInputStyle.Paragraph, "Describe the issue or feature...", maxLength: 3000)]
    public string Description { get; set; } = "";

    [InputLabel("Screenshots (optional)")]
    [ModalFileUpload("screenshots", minValues: 0, maxValues: 10)]
    public IAttachment[] Screenshots { get; set; } = [];
}
```

- [ ] **Step 5: Implement `OutcomeRenderer`** — pure functions from pipeline records to Components V2 message parts, so the module stays thin. All builders come from Discord.Net's CV2 support (`ComponentBuilderV2`, `ContainerBuilder`, `TextDisplayBuilder`, `SectionBuilder`, `ActionRowBuilder`, `MediaGalleryBuilder`). Shape:

```csharp
namespace DiscordGithubBot.Discord;

public static class OutcomeRenderer
{
    /// <summary>Ephemeral response for a pipeline outcome (match found / uncertain list / draft preview).</summary>
    public static MessageComponent Render(ReportOutcome outcome);

    /// <summary>Draft preview with Create/Cancel buttons; regressionOf carries into the create button's custom id.</summary>
    public static MessageComponent RenderDraftPreview(IssueDraft draft, Guid pendingId, int regressionOf = 0);

    /// <summary>Public channel announcement for a created issue.</summary>
    public static MessageComponent RenderAnnouncement(CreatedIssueResult issue, string appName, string reporterDisplayName, ReportType type);

    /// <summary>Ephemeral open-issues list for /issues.</summary>
    public static MessageComponent RenderIssueList(string appName, IReadOnlyList<GitHubIssue> issues);
}
```

Content rules:
- `MatchOpen`: container with text `**This looks like an existing issue:** [#N Title](url)` + action row: button `rep|comment|{id}|{N}` (Primary, "Same issue — add my report") and `rep|draft|{id}|0` (Secondary, "Not it — show my draft").
- `MatchClosed`: text `**This looks like [#N Title](url), closed recently.** Is it still happening in the latest version?` + buttons `rep|stillopen|{id}|{N}` (Primary, "Still happening") and `rep|fixed|{id}|{N}` (Secondary, "Looks fixed").
- `Uncertain`: text `**This might match an existing issue.** Pick one to attach your report, or continue with a new issue.` + select menu (custom id `rep|pick|{id}|0`, one option per candidate: label `#N Title` truncated to 100 chars, value `N`, description = state) + button `rep|draft|{id}|0` (Secondary, "None of these — new issue").
- `NoMatch`: delegate to `RenderDraftPreview` with text `**No existing issue matches. Here's the draft:**`.
- Draft preview: title as bold text, body truncated to 3000 chars in a text display, buttons `rep|create|{id}|{regressionOf}` (Success, "Create issue") and `rep|cancel|{id}|0` (Danger, "Cancel").
- Announcement: container with `**New {bug report|feature request} for {appName}**`, link `[#N Title](url)`, `Reported by {reporter} via Discord`.
- Issue list: `**Open issues — {appName}**` + one `- [#N Title](url)` line per issue (first 25; note `+K more on GitHub` when truncated). Empty list → `No open issues 🎉`.

- [ ] **Step 6: Implement `ReportInteractionModule`** (`InteractionModuleBase<SocketInteractionContext>`). Full flow logic:

```csharp
namespace DiscordGithubBot.Discord;

public class ReportInteractionModule(
    BotOptions options,
    IReportPipeline pipeline,
    AttachmentDownloader downloader,
    IGitHubService gitHub,
    DiscordSocketClient client,
    ILogger<ReportInteractionModule> logger)
    : InteractionModuleBase<SocketInteractionContext>
{
    // --- slash commands ---
    [SlashCommand("report-issue", "Report a bug in the app")]
    public Task ReportIssue([Summary(description: "Which app (only needed when several are configured)")] string? app = null)
        => OpenModalAsync(ReportType.Bug, app);

    [SlashCommand("request-feature", "Request a new feature")]
    public Task RequestFeature([Summary(description: "Which app (only needed when several are configured)")] string? app = null)
        => OpenModalAsync(ReportType.Feature, app);

    private async Task OpenModalAsync(ReportType type, string? appName)
    {
        var resolved = ResolveApp(appName);           // see resolution rules below
        if (resolved.Error is not null) { await RespondAsync(resolved.Error, ephemeral: true); return; }
        var typeToken = type == ReportType.Bug ? "bug" : "feature";
        await RespondWithModalAsync<ReportModal>($"report-modal|{typeToken}|{resolved.App!.Repo}");
    }

    [SlashCommand("issues", "List open GitHub issues")]
    public async Task Issues([Summary(description: "Which app (only needed when several are configured)")] string? app = null)
    {
        var resolved = ResolveApp(app);
        if (resolved.Error is not null) { await RespondAsync(resolved.Error, ephemeral: true); return; }
        await DeferAsync(ephemeral: true);
        var issues = await gitHub.ListIssuesAsync(resolved.App!, "open", null);
        await FollowupAsync(components: OutcomeRenderer.RenderIssueList(resolved.App.Name, issues), ephemeral: true);
    }

    // --- modal submit ---
    [ModalInteraction("report-modal|*|*")]
    public async Task OnReportModal(string typeToken, string repo, ReportModal modal)
    {
        await DeferAsync(ephemeral: true);                       // 3s deadline first
        var app = options.AppByRepo(repo);
        if (app is null) { await FollowupAsync("Unknown app configuration.", ephemeral: true); return; }
        var type = typeToken == "bug" ? ReportType.Bug : ReportType.Feature;
        var (payloads, skipped) = await downloader.DownloadAsync(modal.Screenshots);   // before URLs go stale
        try
        {
            var outcome = await pipeline.ProcessAsync(new ReportSubmission(
                app, type, Context.User.Id, Context.User.GlobalName ?? Context.User.Username,
                modal.Description, payloads));
            var note = skipped.Count > 0 ? $"⚠️ Skipped (not an image / too large / failed): {string.Join(", ", skipped)}\n" : null;
            await FollowupAsync(text: note, components: OutcomeRenderer.Render(outcome), ephemeral: true);
        }
        catch (NormalizationException)
        {
            await FollowupAsync("Sorry — I couldn't process that report right now. Please try again.", ephemeral: true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Report pipeline failed");
            await FollowupAsync("Something went wrong while processing your report. Please try again later.", ephemeral: true);
        }
    }

    // --- component handlers: one per action, each parses via CustomIds.TryParse(((IComponentInteraction)Context.Interaction).Data.CustomId, ...) ---
}
```

App-resolution rules (`ResolveApp(string? appName)` private helper returning `(AppConfig? App, string? Error)`):
1. Apps for this guild = `options.AppsForGuild(Context.Guild.Id)`; empty → error `"No app is configured for this server."`.
2. `appName` given → match by `Name` (OrdinalIgnoreCase) within guild apps; no match → error listing valid names.
3. `appName` null: exactly 1 guild app → use it; several → error `"Several apps are configured here — run the command again with app: one of {names}."`.

Component handlers (all: `DeferAsync(ephemeral: true)` first, then act, `FollowupAsync(..., ephemeral: true)`; wrap in try/catch → on `ExpiredPendingReportException` reply `"This report session has expired — please run the command again."`; on other exceptions log + generic ephemeral error):
- `[ComponentInteraction("rep|create|*|*")]` → `pipeline.CreateIssueAsync(id, issueNumber == 0 ? null : issueNumber)` → announce via `AnnounceAsync` (below) → followup `✅ Created [#N Title](url)`.
- `[ComponentInteraction("rep|cancel|*|*")]` → `pipeline.CancelAsync(id)` → followup `Cancelled — nothing was created.`
- `[ComponentInteraction("rep|comment|*|*")]` → `pipeline.AddCommentAsync(id, issueNumber)` → followup `💬 Added your report to [#N](commentUrl)`.
- `[ComponentInteraction("rep|draft|*|*")]` and `rep|stillopen` → respond with `OutcomeRenderer.RenderDraftPreview(draft, id, regressionOf)` — the draft title/body come from the pending report via `pipeline.PeekAsync(id)` (Task 10; null → expired message). `stillopen` passes `regressionOf = issueNumber`; `draft` passes `0`.
- `[ComponentInteraction("rep|fixed|*|*")]` → `pipeline.CancelAsync(id)` → followup `Glad it's fixed! Reference: [#N](https://github.com/{repo}/issues/{N})` — repo taken from the pending report before cancelling (via `PeekAsync`; if already expired just confirm cancellation).
- `[ComponentInteraction("rep|pick|*|*")]` (select menu) → selected value = issue number string (method signature `(string action..., string[] selections)` per Discord.Net select handling); look the candidate up in the pending report's `CandidatesJson` (via `PeekAsync` + `JsonSerializer.Deserialize<List<CandidateIssue>>`); candidate `State == "open"` → render the MatchOpen container for it; closed → render the MatchClosed container.

`AnnounceAsync(AppConfig app, CreatedIssueResult result, ReportType type)` private helper: for each `app.ChannelIds`, `client.GetChannel(channelId) as IMessageChannel` → `SendMessageAsync(components: OutcomeRenderer.RenderAnnouncement(...))`; log a warning for unknown channels, never throw.

- [ ] **Step 7: Implement `BotService`** (hosted service):

```csharp
namespace DiscordGithubBot.Discord;

public sealed class BotService(
    DiscordSocketClient client,
    InteractionService interactions,
    IServiceProvider services,
    BotOptions options,
    ILogger<BotService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        client.Log += msg => { logger.LogInformation("{Discord}", msg.ToString()); return Task.CompletedTask; };
        interactions.Log += msg => { logger.LogInformation("{Interactions}", msg.ToString()); return Task.CompletedTask; };

        await interactions.AddModulesAsync(typeof(BotService).Assembly, services);

        client.Ready += async () =>
        {
            foreach (var guildId in options.Apps.SelectMany(a => a.GuildIds).Distinct())
            {
                try { await interactions.RegisterCommandsToGuildAsync(guildId); }
                catch (Exception ex) { logger.LogError(ex, "Failed to register commands for guild {GuildId}", guildId); }
            }
            logger.LogInformation("Slash commands registered.");
        };

        client.InteractionCreated += async interaction =>
        {
            var ctx = new SocketInteractionContext(client, interaction);
            await interactions.ExecuteCommandAsync(ctx, services);
        };

        await client.LoginAsync(TokenType.Bot, options.Discord.Token);
        await client.StartAsync();
        await Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => Task.CompletedTask);
        await client.StopAsync();
    }
}
```

- [ ] **Step 8: Build + run codec tests** (`dotnet build && dotnet test`). The Discord module itself is exercised manually in Task 14's checklist; its logic is thin by design.

- [ ] **Step 9: Commit** — `feat: add Discord slash commands, report modal with file upload, and CV2 outcome rendering`

---

### Task 12: Host wiring (Program.cs), maintenance service, smoke-upload mode

**Files:**
- Create: `src/DiscordGithubBot/HostSetup.cs` (DI extension), `src/DiscordGithubBot/MaintenanceService.cs`
- Modify: `src/DiscordGithubBot/Program.cs` (replace stub)
- Test: `tests/DiscordGithubBot.Tests/HostSetupTests.cs`

**Interfaces:**
- Consumes: everything.
- Produces: `IServiceCollection AddBotServices(this IServiceCollection services, BotOptions options)` in `namespace DiscordGithubBot;` — registers every service below; used by Program.cs and by the DI test.

- [ ] **Step 1: Write failing DI test** `tests/DiscordGithubBot.Tests/HostSetupTests.cs`:

```csharp
using DiscordGithubBot;
using DiscordGithubBot.Configuration;
using DiscordGithubBot.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace DiscordGithubBot.Tests;

public class HostSetupTests
{
    [Fact]
    public void All_pipeline_services_resolve()
    {
        var options = new BotOptions
        {
            Discord = new() { Token = "t" }, OpenAI = new() { ApiKey = "k" },
            Database = new() { Path = Path.Combine(Path.GetTempPath(), $"di-test-{Guid.NewGuid():N}.db") },
            Apps = [new AppConfig { Name = "A", Repo = "o/r", GitHubToken = "p", GuildIds = [1UL], ChannelIds = [2UL] }],
        };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBotServices(options);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IReportPipeline>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IIssueSyncService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IPendingReportStore>());
    }
}
```

- [ ] **Step 2: Run test, verify compile failure.**

- [ ] **Step 3: Implement `HostSetup.AddBotServices`:**

```csharp
using DiscordGithubBot.Ai;
using DiscordGithubBot.Configuration;
using DiscordGithubBot.Data;
using DiscordGithubBot.Discord;
using DiscordGithubBot.GitHub;
using DiscordGithubBot.Pipeline;
using global::Discord.Interactions;
using global::Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using OpenAI;

namespace DiscordGithubBot;

public static class HostSetup
{
    public static IServiceCollection AddBotServices(this IServiceCollection services, BotOptions options)
    {
        services.AddSingleton(options);

        // database
        services.AddDbContext<BotDbContext>(o => o.UseSqlite($"Data Source={options.Database.Path}"));

        // GitHub over HttpClient
        services.AddHttpClient<IGitHubService, GitHubService>(ConfigureGitHubClient);
        services.AddHttpClient<IImageUploader, GitHubImageUploader>(ConfigureGitHubClient);
        services.AddHttpClient<AttachmentDownloader>();

        // AI (OpenAIClient construction is lazy and network-free; startup validation guarantees a key,
        // and the DI test passes a dummy key)
        var openAi = new OpenAIClient(options.OpenAI.ApiKey);
        services.AddSingleton(openAi.GetChatClient(options.OpenAI.ChatModel).AsIChatClient());
        services.AddSingleton(openAi.GetEmbeddingClient(options.OpenAI.EmbeddingModel).AsIEmbeddingGenerator());

        // pipeline
        services.AddScoped<IReportNormalizer, ReportNormalizer>();
        services.AddScoped<IDuplicateJudge, DuplicateJudge>();
        services.AddScoped<IIssueSyncService, IssueSyncService>();
        services.AddScoped<IPendingReportStore, PendingReportStore>();
        services.AddScoped<IReportPipeline, ReportPipeline>();

        // discord
        services.AddSingleton(new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds,
        }));
        services.AddSingleton(sp => new InteractionService(
            sp.GetRequiredService<DiscordSocketClient>(),
            new InteractionServiceConfig { UseCompiledLambda = true }));
        services.AddHostedService<BotService>();
        services.AddHostedService<MaintenanceService>();
        return services;
    }

    private static void ConfigureGitHubClient(HttpClient http)
    {
        http.BaseAddress = new Uri("https://api.github.com/");
        http.DefaultRequestHeaders.UserAgent.ParseAdd("discord-gh-issue-bot");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }
}
```
- [ ] **Step 4: Implement `MaintenanceService`:**

```csharp
namespace DiscordGithubBot;

/// <summary>Hourly cleanup of expired pending reports.</summary>
public sealed class MaintenanceService(IServiceScopeFactory scopes, ILogger<MaintenanceService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var removed = await scope.ServiceProvider.GetRequiredService<IPendingReportStore>()
                    .CleanupExpiredAsync(stoppingToken);
                if (removed > 0) logger.LogInformation("Cleaned up {Count} expired pending reports", removed);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { logger.LogWarning(ex, "Pending-report cleanup failed"); }
        }
    }
}
```

- [ ] **Step 5: Implement `Program.cs`:**

```csharp
using DiscordGithubBot;
using DiscordGithubBot.Configuration;
using DiscordGithubBot.Data;
using DiscordGithubBot.GitHub;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);

// config layering: appsettings.json + appsettings.{Env}.json + env vars come from
// CreateApplicationBuilder; Docker secrets (key-per-file) are added LAST so they win.
if (Directory.Exists("/run/secrets"))
    builder.Configuration.AddKeyPerFile("/run/secrets", optional: true);

var options = builder.Configuration.Get<BotOptions>() ?? new BotOptions();
var errors = options.Validate();
if (errors.Count > 0)
{
    foreach (var e in errors) Console.Error.WriteLine($"CONFIG ERROR: {e}");
    return 1;
}

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.Database.Path))!);
builder.Services.AddBotServices(options);

var host = builder.Build();

using (var scope = host.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<BotDbContext>().Database.EnsureCreated();

// one-shot smoke test for the unofficial upload endpoint: dotnet run -- --smoke-upload owner/repo
if (args is ["--smoke-upload", var repo])
{
    var app = options.AppByRepo(repo);
    if (app is null) { Console.Error.WriteLine($"No configured app for repo '{repo}'."); return 1; }
    var uploader = host.Services.GetRequiredService<IImageUploader>();
    var png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
    var result = await uploader.UploadAsync(app, "smoke-test.png", "image/png", png);
    Console.WriteLine(result is null ? "SMOKE FAILED: both tiers failed" : $"SMOKE OK: {result.Url}");
    return result is null ? 1 : 0;
}

await host.RunAsync();
return 0;
```

- [ ] **Step 6: Run `dotnet build && dotnet test`** — everything green.

- [ ] **Step 7: Commit** — `feat: wire host, DI, config layering with docker secrets, maintenance cleanup and smoke-upload mode`

---

### Task 13: Docker

**Files:**
- Create: `Dockerfile`, `.dockerignore`, `docker-compose.yml`

- [ ] **Step 1: Write `Dockerfile`** (multi-stage, non-root, .NET 10 = Ubuntu Noble images):

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY DiscordGithubBot.sln .
COPY src/DiscordGithubBot/DiscordGithubBot.csproj src/DiscordGithubBot/
RUN dotnet restore src/DiscordGithubBot
COPY src/ src/
RUN dotnet publish src/DiscordGithubBot -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
# db volume must be writable by the non-root app user BEFORE switching user
RUN mkdir /data && chown $APP_UID /data
USER $APP_UID
COPY --from=build /app .
ENV Database__Path=/data/app.db
ENTRYPOINT ["dotnet", "DiscordGithubBot.dll"]
```

- [ ] **Step 2: Write `.dockerignore`:**

```
bin/
obj/
db/
secrets/
.env
.git/
.vs/
TestResults/
docs/
tests/
*.md
```

- [ ] **Step 3: Write `docker-compose.yml`:**

```yaml
services:
  bot:
    build: .
    restart: unless-stopped
    env_file:
      - path: .env
        required: false
    environment:
      DOTNET_ENVIRONMENT: Production
    volumes:
      - botdata:/data
    secrets:
      - Discord__Token
      - OpenAI__ApiKey

volumes:
  botdata:

secrets:
  Discord__Token:
    file: ./secrets/Discord__Token
  OpenAI__ApiKey:
    file: ./secrets/OpenAI__ApiKey
```
Note in APP.md: per-app GitHub PATs can also be file-secrets (`Apps__0__GitHubToken` as a secret file name) or come from `.env`; `secrets/` is gitignored; compose secret files are optional to create but referenced ones must exist — document that users who keep everything in `.env` should delete the `secrets:` blocks.

- [ ] **Step 4: Verify:** `docker build -t discord-gh-issue-bot .` succeeds (if Docker is unavailable in the environment, verify `dotnet publish -c Release` succeeds and note the skip in the commit message).

- [ ] **Step 5: Commit** — `feat: add Dockerfile, dockerignore and docker-compose with secrets and db volume`

---

### Task 14: Finalize docs + full verification

**Files:**
- Modify: `APP.md`, `CLAUDE.md`, `docs/DECISIONS.md`, `.env.example`

- [ ] **Step 1: Reconcile docs with reality.** Re-read `APP.md`, `CLAUDE.md`, `docs/DECISIONS.md`, `.env.example` against the implemented code: command names/options, config keys, Docker instructions, smoke-upload usage. Fix every drift. Add any decision made during Tasks 2–13 that isn't recorded yet (e.g. builder-name renames, version substitutions, the issue-assets branch simplification).

- [ ] **Step 2: Full verification:** `dotnet build && dotnet test` — all green.

- [ ] **Step 3: Write the manual test checklist** into `APP.md` under `## Manual verification` (bot must be run with real tokens):
  1. `/report-issue` in a guild with 1 app: modal opens without app friction; submit with 2 screenshots.
  2. New issue path: preview shows AI draft → Create → issue on GitHub has screenshots inline, `bug` label, reporter credit; announcement lands in the configured channel; invoker sees only ephemeral messages.
  3. Duplicate path: report the same bug twice → match presented → "Same issue" → comment appears on the first issue.
  4. Closed-issue path: close the issue on GitHub, report it again → "still happening?" → new issue references the closed one.
  5. `/issues` lists open issues with working links.
  6. `dotnet run -- --smoke-upload owner/repo` prints `SMOKE OK` (or documents that the PAT can't use the unofficial endpoint and the fallback engaged).
- [ ] **Step 4: Commit** — `docs: reconcile documentation with implementation and add manual verification checklist`
