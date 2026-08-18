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

    [Fact]
    public async Task Deleting_a_report_takes_its_attachment_blobs_with_it()
    {
        var expired = Guid.NewGuid();
        var live = Guid.NewGuid();
        await _sut.SaveAsync(Report(expired, DateTime.UtcNow.AddHours(-2)));
        await _sut.SaveAsync(Report(live, DateTime.UtcNow));

        await _sut.CleanupExpiredAsync();
        Assert.Equal(1, _db.PendingAttachments.Count());

        await _sut.DeleteAsync(live);
        Assert.Empty(_db.PendingAttachments.ToList());
    }

    public void Dispose() { _db.Dispose(); _conn.Dispose(); }
}
