using Microsoft.EntityFrameworkCore;

namespace DiscordGithubBot.Data;

/// <summary>What <see cref="DatabaseSchema.EnsureCurrent"/> had to do to the file.</summary>
public enum SchemaChange
{
    /// <summary>The schema was current (or the file was new).</summary>
    None,

    /// <summary>Additive upgrades were applied; every row was kept.</summary>
    Upgraded,

    /// <summary>No upgrade path existed: every table was dropped and recreated.</summary>
    Rebuilt,
}

/// <summary>
/// Keeps the SQLite file on the schema this build expects. The bot ships no migrations (decision 12) and
/// <c>EnsureCreated</c> never alters a database that already has tables, so the schema carries a version in
/// SQLite's own <c>PRAGMA user_version</c>. A file one or more versions behind is brought forward by the
/// additive steps in <see cref="Upgrades"/> when there are steps for every version in between — that is what
/// keeps an expensive cache such as the repository map across an upgrade. Otherwise every table is dropped and
/// recreated, which is safe because nothing in the file is precious: the issue cache and the repository map
/// rebuild themselves, and pending drafts live for an hour.
/// </summary>
public static class DatabaseSchema
{
    /// <summary>
    /// Bump on any change to an entity, column, index or relationship — and, for a purely additive change, add
    /// the step from the previous version to <see cref="Upgrades"/> so existing data survives. 0 is an
    /// unstamped file, which covers every database written before the stamp existed.
    /// </summary>
    public const int Version = 6;

    /// <summary>
    /// SQL that moves a file from the key version to the next one without losing rows. Only additive steps
    /// belong here (new tables, new columns with defaults); anything else is a rebuild.
    /// </summary>
    private static readonly Dictionary<long, string[]> Upgrades = new()
    {
        // 5 -> 6: repository-map embeddings.
        [5] =
        [
            "ALTER TABLE \"RepoFiles\" ADD COLUMN \"Embedding\" BLOB NOT NULL DEFAULT x'';",
            "ALTER TABLE \"RepoFiles\" ADD COLUMN \"EmbeddingModel\" TEXT NOT NULL DEFAULT '';",
        ],
    };

    public static SchemaChange EnsureCurrent(BotDbContext db)
    {
        db.Database.OpenConnection();
        try
        {
            var version = ReadVersion(db);
            if (version == Version)
            {
                db.Database.EnsureCreated();
                return SchemaChange.None;
            }

            if (version > 0 && version < Version && HasUpgradePath(version))
            {
                using var transaction = db.Database.GetDbConnection().BeginTransaction();
                for (var v = version; v < Version; v++)
                    foreach (var sql in Upgrades[v]) Execute(db, sql, transaction);
                Execute(db, $"PRAGMA user_version = {Version};", transaction);
                transaction.Commit();
                return SchemaChange.Upgraded;
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
            return tables.Count > 0 ? SchemaChange.Rebuilt : SchemaChange.None;
        }
        finally
        {
            db.Database.CloseConnection();
        }
    }

    private static bool HasUpgradePath(long from)
    {
        for (var v = from; v < Version; v++)
            if (!Upgrades.ContainsKey(v)) return false;
        return true;
    }

    private static long ReadVersion(BotDbContext db) => (long)Query(db, "PRAGMA user_version").Single()!;

    /// <summary>
    /// Plain ADO on the open connection. Table names come from <c>sqlite_master</c> and cannot be bound as
    /// parameters, and neither can a pragma's value, so these statements are built as text.
    /// </summary>
    private static void Execute(BotDbContext db, string sql, System.Data.Common.DbTransaction? transaction = null)
    {
        using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
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
