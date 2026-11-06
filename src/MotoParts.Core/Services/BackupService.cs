using System.Globalization;
using Microsoft.Data.Sqlite;
using MotoParts.Core.Common;
using MotoParts.Core.Data;

namespace MotoParts.Core.Services;

/// <summary>
/// Backups of the single database file.
///
/// The whole business lives in one file, so this is the shop's protection
/// against a failed disk or an accidental delete. Backups use SQLite's online
/// backup, which produces a consistent snapshot while the system is in use.
/// Restoring checks the candidate file first and keeps the database it replaces,
/// so a restore can itself be undone.
/// </summary>
public sealed class BackupService
{
    private const string Prefix = "motoparts-";
    private const string Extension = ".db";

    private readonly Database _database;
    private readonly IClock _clock;

    public BackupService(Database database, IClock clock)
    {
        _database = database;
        _clock = clock;
    }

    /// <summary>Takes a backup into <paramref name="folder"/> and returns its path.</summary>
    public string Create(string folder, string label)
    {
        Directory.CreateDirectory(folder);

        var stamp = _clock.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
        var safeLabel = new string(label.Where(char.IsLetterOrDigit).ToArray());
        var path = Path.Combine(folder, $"{Prefix}{stamp}-{safeLabel}{Extension}");

        _database.BackupTo(path);
        return path;
    }

    /// <summary>Keeps the newest <paramref name="keep"/> backups in a folder and deletes the rest.</summary>
    public int Prune(string folder, int keep)
    {
        if (keep < 1 || !Directory.Exists(folder))
        {
            return 0;
        }

        var stale = List(folder).Skip(keep).ToList();

        foreach (var backup in stale)
        {
            File.Delete(backup.Path);
        }

        return stale.Count;
    }

    public IReadOnlyList<BackupFile> List(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }

        return Directory.GetFiles(folder, $"{Prefix}*{Extension}")
            .Select(p => new FileInfo(p))
            .OrderByDescending(f => f.LastWriteTime)
            .Select(f => new BackupFile(f.FullName, f.Name, f.LastWriteTime, f.Length))
            .ToList();
    }

    /// <summary>The time of the newest backup in a folder, or null.</summary>
    public DateTime? LastBackupAt(string folder) => List(folder).FirstOrDefault()?.CreatedAt;

    /// <summary>
    /// Opens a candidate backup read-only and confirms it is a sound database
    /// from this system. Returns null when valid, otherwise the reason.
    /// </summary>
    public static string? Validate(string path)
    {
        if (!File.Exists(path))
        {
            return "الملف غير موجود.";
        }

        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());

            connection.Open();

            using var integrity = connection.CreateCommand();
            integrity.CommandText = "PRAGMA integrity_check";

            if (integrity.ExecuteScalar() as string != "ok")
            {
                return "الملف تالف ولا يجتاز فحص السلامة.";
            }

            using var schema = connection.CreateCommand();
            schema.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('schema_migrations', 'journal_entries', 'sales')";

            if ((long)schema.ExecuteScalar()! != 3)
            {
                return "الملف ليس نسخة احتياطية من هذا النظام.";
            }

            return null;
        }
        catch (SqliteException ex)
        {
            return $"تعذر فتح الملف: {ex.Message}";
        }
    }

    /// <summary>
    /// Replaces the live database with a backup. The caller must have closed
    /// every connection (the application restarts afterwards). The replaced
    /// database is kept beside the backups as a safety copy.
    /// </summary>
    public string Restore(string backupPath, string safetyFolder)
    {
        var problem = Validate(backupPath);

        if (problem is not null)
        {
            throw new BusinessRuleException(problem);
        }

        SqliteConnection.ClearAllPools();

        var safety = Create(safetyFolder, "before-restore");

        // Taking the safety copy opened a pooled connection; close it, or the
        // write-ahead log stays locked. The log must go: replayed onto the
        // restored file it would corrupt it.
        SqliteConnection.ClearAllPools();

        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var sidecar = _database.FilePath + suffix;

            if (File.Exists(sidecar))
            {
                File.Delete(sidecar);
            }
        }

        File.Copy(backupPath, _database.FilePath, overwrite: true);
        return safety;
    }
}

public sealed record BackupFile(string Path, string Name, DateTime CreatedAt, long SizeBytes)
{
    public string SizeLabel => SizeBytes >= 1024 * 1024
        ? $"{SizeBytes / 1024d / 1024d:0.0} MB"
        : $"{SizeBytes / 1024d:0} KB";
}
