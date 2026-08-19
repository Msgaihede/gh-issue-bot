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
