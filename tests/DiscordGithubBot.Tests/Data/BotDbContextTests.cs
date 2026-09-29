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

    public void Dispose() => _conn.Dispose();

    private BotDbContext NewContext()
    {
        var ctx = new BotDbContext(new DbContextOptionsBuilder<BotDbContext>()
            .UseSqlite(_conn).Options);
        ctx.Database.EnsureCreated();
        return ctx;
    }

    [Fact]
    public void A_cached_issue_round_trips()
    {
        using (var ctx = NewContext())
        {
            ctx.CachedIssues.Add(new CachedIssue
            {
                RepoKey = "owner/repo", IssueNumber = 7, Title = "Crash on live",
                UpdatedAtUtc = DateTime.UtcNow, BodyExcerpt = "It crashes.", HtmlUrl = "https://x/7",
            });
            ctx.SaveChanges();
        }
        using (var ctx = NewContext())
        {
            var e = ctx.CachedIssues.Single();
            Assert.Equal("Crash on live", e.Title);
            Assert.Equal("It crashes.", e.BodyExcerpt);
        }
    }

    [Fact]
    public void Duplicate_repo_and_issue_number_violates_unique_index()
    {
        using var ctx = NewContext();
        ctx.CachedIssues.AddRange(
            new CachedIssue { RepoKey = "o/r", IssueNumber = 1, Title = "a" },
            new CachedIssue { RepoKey = "o/r", IssueNumber = 1, Title = "b" });
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
