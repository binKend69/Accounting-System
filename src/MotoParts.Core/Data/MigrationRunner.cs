using System.Reflection;
using Microsoft.Data.Sqlite;

namespace MotoParts.Core.Data;

/// <summary>
/// Brings a database file up to the schema this build expects.
///
/// Migrations are the numbered .sql files embedded in this assembly, applied in
/// order, each in its own transaction and recorded in schema_migrations. An
/// existing database is backed up before the first pending migration runs, so
/// an upgrade that goes wrong can always be undone by restoring that copy.
/// </summary>
public sealed class MigrationRunner
{
    private const string ResourcePrefix = "MotoParts.Core.Data.Migrations.";

    private readonly Database _database;

    public MigrationRunner(Database database)
    {
        _database = database;
    }

    /// <summary>Embedded migrations as (version, sql), in application order.</summary>
    public static IReadOnlyList<(string Version, string Sql)> AvailableMigrations()
    {
        var assembly = typeof(MigrationRunner).Assembly;

        return assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n => (Version: n[ResourcePrefix.Length..^".sql".Length], Sql: ReadResource(assembly, n)))
            .ToList();
    }

    public IReadOnlyList<string> PendingMigrations()
    {
        var applied = AppliedVersions();
        return AvailableMigrations().Select(m => m.Version).Where(v => !applied.Contains(v)).ToList();
    }

    /// <summary>
    /// Applies every pending migration. Returns the versions applied.
    /// </summary>
    /// <param name="backupPathForUpgrade">
    /// Where to snapshot an existing database before changing it. Ignored for a
    /// brand-new database, which has nothing to lose.
    /// </param>
    public IReadOnlyList<string> Migrate(string? backupPathForUpgrade = null)
    {
        var isNewDatabase = !_database.Exists;

        using var connection = _database.Open();
        EnsureHistoryTable(connection);

        var applied = AppliedVersions(connection);
        var pending = AvailableMigrations().Where(m => !applied.Contains(m.Version)).ToList();

        if (pending.Count == 0)
        {
            return [];
        }

        if (!isNewDatabase && applied.Count > 0 && backupPathForUpgrade is not null)
        {
            connection.Close();
            _database.BackupTo(backupPathForUpgrade);
        }

        using var work = isNewDatabase ? connection : _database.Open();
        var done = new List<string>();

        foreach (var (version, sql) in pending)
        {
            using var transaction = work.BeginTransaction(deferred: false);

            try
            {
                using (var command = work.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = sql;
                    command.ExecuteNonQuery();
                }

                using (var record = work.CreateCommand())
                {
                    record.Transaction = transaction;
                    record.CommandText = "INSERT INTO schema_migrations (version, applied_at) VALUES ($v, datetime('now', 'localtime'))";
                    record.Parameters.AddWithValue("$v", version);
                    record.ExecuteNonQuery();
                }

                transaction.Commit();
                done.Add(version);
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                throw new InvalidOperationException($"Migration {version} failed: {ex.Message}", ex);
            }
        }

        return done;
    }

    private HashSet<string> AppliedVersions()
    {
        if (!_database.Exists)
        {
            return [];
        }

        using var connection = _database.Open();
        EnsureHistoryTable(connection);
        return AppliedVersions(connection);
    }

    private static HashSet<string> AppliedVersions(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM schema_migrations";

        var versions = new HashSet<string>(StringComparer.Ordinal);

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            versions.Add(reader.GetString(0));
        }

        return versions;
    }

    private static void EnsureHistoryTable(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version    TEXT PRIMARY KEY,
                applied_at TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    private static string ReadResource(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing embedded migration {name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
