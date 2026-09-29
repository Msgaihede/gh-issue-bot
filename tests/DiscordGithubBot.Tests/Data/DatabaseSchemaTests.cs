using DiscordGithubBot.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DiscordGithubBot.Tests.Data;

public sealed class DatabaseSchemaTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"schema-test-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_path)) File.Delete(_path);
    }

    private BotDbContext Context() =>
        new(new DbContextOptionsBuilder<BotDbContext>().UseSqlite($"Data Source={_path}").Options);

    private long UserVersion()
    {
        using var conn = new SqliteConnection($"Data Source={_path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        return (long)cmd.ExecuteScalar()!;
    }

    private void Exec(string sql)
    {
        using var conn = new SqliteConnection($"Data Source={_path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void A_new_database_is_created_and_stamped()
    {
        using (var db = Context()) Assert.False(DatabaseSchema.EnsureCurrent(db));

        Assert.Equal(DatabaseSchema.Version, UserVersion());
        using (var db = Context()) Assert.Empty(db.CachedIssues.ToList());
    }

    [Fact]
    public void A_current_database_keeps_its_rows()
    {
        using (var db = Context())
        {
            DatabaseSchema.EnsureCurrent(db);
            db.CachedIssues.Add(new CachedIssue { RepoKey = "o/r", IssueNumber = 1, Title = "kept" });
            db.SaveChanges();
        }

        using (var db = Context())
        {
            Assert.False(DatabaseSchema.EnsureCurrent(db));
            Assert.Equal("kept", db.CachedIssues.Single().Title);
        }
    }

    /// <summary>
    /// The case this exists for: a Docker volume holding the previous build's file. EnsureCreated alone would
    /// see tables, do nothing, and leave the first query to fail on a table that is not there.
    /// </summary>
    [Fact]
    public void An_unstamped_database_from_an_older_build_is_rebuilt()
    {
        Exec("CREATE TABLE IssueEmbeddings (Id INTEGER PRIMARY KEY, Vector BLOB);");

        using (var db = Context()) Assert.True(DatabaseSchema.EnsureCurrent(db));

        Assert.Equal(DatabaseSchema.Version, UserVersion());
        using (var db = Context())
        {
            db.CachedIssues.Add(new CachedIssue { RepoKey = "o/r", IssueNumber = 1, Title = "works" });
            db.SaveChanges();
        }
    }
}
