using Microsoft.Data.Sqlite;
using MotoParts.Core.Data;

namespace MotoParts.Tests.Data;

public sealed class MigrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "motoparts-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Every_migration_is_embedded_in_order()
    {
        var versions = MigrationRunner.AvailableMigrations().Select(m => m.Version).ToList();

        Assert.Equal(
            ["0001_foundation", "0002_accounting", "0003_inventory", "0004_shifts",
             "0005_sales", "0006_purchasing", "0007_vouchers", "0008_payroll"],
            versions);
    }

    [Fact]
    public void A_fresh_database_migrates_and_a_second_run_is_a_no_op()
    {
        var database = new Database(Path.Combine(_directory, "fresh.db"));
        var runner = new MigrationRunner(database);

        var applied = runner.Migrate();

        Assert.Equal(8, applied.Count);
        Assert.Empty(runner.PendingMigrations());
        Assert.Empty(runner.Migrate());
    }

    [Fact]
    public void Foreign_keys_are_enforced_on_every_connection()
    {
        var database = new Database(Path.Combine(_directory, "fk.db"));
        new MigrationRunner(database).Migrate();

        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys";

        Assert.Equal(1L, command.ExecuteScalar());
    }

    [Fact]
    public void The_audit_log_cannot_be_edited_or_deleted()
    {
        var database = new Database(Path.Combine(_directory, "audit.db"));
        new MigrationRunner(database).Migrate();

        using var connection = database.Open();
        Exec(connection, "INSERT INTO audit_logs (auditable_type, action, created_at) VALUES ('Test', 'Created', '2026-01-01 10:00:00')");

        var update = Assert.Throws<SqliteException>(() => Exec(connection, "UPDATE audit_logs SET action = 'Deleted'"));
        Assert.Contains("سجل التدقيق", update.Message);

        var delete = Assert.Throws<SqliteException>(() => Exec(connection, "DELETE FROM audit_logs"));
        Assert.Contains("سجل التدقيق", delete.Message);
    }

    [Fact]
    public void An_upgrade_backs_up_the_existing_database_first()
    {
        var path = Path.Combine(_directory, "upgrade.db");
        var database = new Database(path);

        // Simulate a database that has only the first migration applied.
        using (var connection = database.Open())
        {
            Exec(connection, "CREATE TABLE schema_migrations (version TEXT PRIMARY KEY, applied_at TEXT NOT NULL)");
            Exec(connection, MigrationRunner.AvailableMigrations()[0].Sql);
            Exec(connection, "INSERT INTO schema_migrations VALUES ('0001_foundation', '2026-01-01')");
            Exec(connection, "INSERT INTO settings (key, value) VALUES ('marker', 'before-upgrade')");
        }

        var backup = Path.Combine(_directory, "pre-upgrade.db");
        var applied = new MigrationRunner(database).Migrate(backup);

        Assert.Equal(7, applied.Count);
        Assert.True(File.Exists(backup));

        using var restored = new SqliteConnection($"Data Source={backup};Pooling=False");
        restored.Open();
        using var command = restored.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = 'marker'";
        Assert.Equal("before-upgrade", command.ExecuteScalar());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
