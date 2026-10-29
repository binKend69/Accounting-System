using Microsoft.Data.Sqlite;

namespace MotoParts.Core.Data;

/// <summary>
/// Where the database file lives, and how every connection to it is opened.
///
/// The pragmas here are not optional tuning. SQLite ships with foreign keys
/// switched OFF per connection; without <c>foreign_keys = ON</c> every
/// REFERENCES clause in the schema would be decoration. Opening all connections
/// through this class is what makes the constraints real.
/// </summary>
public sealed class Database
{
    public Database(string filePath)
    {
        FilePath = Path.GetFullPath(filePath);

        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // One shared cache per process would let a stale page be read
            // across connections; private caches are the safe default.
            Cache = SqliteCacheMode.Private,
            Pooling = true,
        }.ToString();
    }

    public string FilePath { get; }

    public string ConnectionString { get; }

    public bool Exists => File.Exists(FilePath);

    public SqliteConnection Open()
    {
        var directory = Path.GetDirectoryName(FilePath);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var pragma = connection.CreateCommand();
        pragma.CommandText = """
            PRAGMA foreign_keys = ON;
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = FULL;
            PRAGMA busy_timeout = 15000;
            PRAGMA recursive_triggers = OFF;
            """;
        pragma.ExecuteNonQuery();

        return connection;
    }

    /// <summary>
    /// Copies the live database to <paramref name="destinationPath"/> using
    /// SQLite's online backup, which produces a consistent snapshot even while
    /// the database is in use - unlike copying the file, which can capture a
    /// half-written page.
    /// </summary>
    public void BackupTo(string destinationPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath));

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(destinationPath))
        {
            File.Delete(destinationPath);
        }

        using var source = Open();
        using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());

        destination.Open();
        source.BackupDatabase(destination);

        // Leave a single self-contained file, not a WAL pair.
        using var checkpoint = destination.CreateCommand();
        checkpoint.CommandText = "PRAGMA journal_mode = DELETE;";
        checkpoint.ExecuteNonQuery();
    }
}
