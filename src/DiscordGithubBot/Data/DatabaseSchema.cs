using Microsoft.EntityFrameworkCore;

namespace DiscordGithubBot.Data;

/// <summary>
/// Keeps the SQLite file on the schema this build expects. The bot ships no migrations (decision 12) and
/// <c>EnsureCreated</c> never alters a database that already has tables, so every schema change used to
/// mean "delete your SQLite file by hand" — easy to miss on a Docker volume, and fatal on the first query.
/// Instead the schema carries a version in SQLite's own <c>PRAGMA user_version</c>; on a mismatch every table
/// is dropped and recreated. That is safe because nothing in the file is precious: the issue cache and the
/// repository map rebuild themselves, and pending drafts live for an hour.
/// </summary>
public static class DatabaseSchema
{
    /// <summary>
    /// Bump on any change to an entity, column, index or relationship. 0 is an unstamped file, which covers
    /// every database written before the stamp existed.
    /// </summary>
    public const int Version = 5;

    /// <returns>true when an existing schema was dropped and rebuilt.</returns>
    public static bool EnsureCurrent(BotDbContext db)
    {
        db.Database.OpenConnection();
        try
        {
            if (ReadVersion(db) == Version)
            {
                db.Database.EnsureCreated();
                return false;
            }

            var tables = Query(db, "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'")
                .Select(v => (string)v!)
                .ToList();

            // Foreign keys off for the drop: tables go in whatever order sqlite_master lists them.
            Execute(db, "PRAGMA foreign_keys = OFF;");
            foreach (var table in tables) Execute(db, $"DROP TABLE IF EXISTS \"{table.Replace("\"", "\"\"")}\";");
            Execute(db, "PRAGMA foreign_keys = ON;");

            db.Database.EnsureCreated();
            Execute(db, $"PRAGMA user_version = {Version};");
            return tables.Count > 0;
        }
        finally
        {
            db.Database.CloseConnection();
        }
    }

    private static long ReadVersion(BotDbContext db) => (long)Query(db, "PRAGMA user_version").Single()!;

    /// <summary>
    /// Plain ADO on the open connection. Table names come from <c>sqlite_master</c> and cannot be bound as
    /// parameters, and neither can a pragma's value, so these statements are built as text.
    /// </summary>
    private static void Execute(BotDbContext db, string sql)
    {
        using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>First column of every row, over the context's already-open connection.</summary>
    private static List<object?> Query(BotDbContext db, string sql)
    {
        using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;

        var values = new List<object?>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) values.Add(reader.GetValue(0));
        return values;
    }
}
