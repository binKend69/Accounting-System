using System.Globalization;
using HisabTak.Application.Backup;
using HisabTak.Application.Security;
using HisabTak.Domain.Security;
using HisabTak.Domain.Setup;
using HisabTak.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace HisabTak.Infrastructure.Backup;

/// <summary>Native COPY_ONLY backup and restore to a new database; never replaces an existing database.</summary>
public sealed class SqlServerBackupService(HisabTakDbContext database, IAuthorizationService authorization) : IDatabaseBackupService
{
    public async Task CreateAsync(int actorId, string serverBackupPath, CancellationToken cancellationToken = default)
    {
        await authorization.DemandAsync(actorId, PermissionCodes.BackupCreate, cancellationToken);
        ValidatePath(serverBackupPath);
        var builder = new SqlConnectionStringBuilder(database.Database.GetConnectionString());
        var sourceDatabase = builder.InitialCatalog;
        builder.InitialCatalog = "master"; // Recovery file locks share one namespace across branch databases.
        builder.Pooling = false; // Closing this dedicated connection releases its session application lock.
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await AcquireSessionLockAsync(connection, "HisabTak.Backup:" + serverBackupPath.ToUpperInvariant(), cancellationToken);
        if (await FileExistsAsync(connection, serverBackupPath, cancellationToken))
            throw new InvalidOperationException("Choose a new backup file; existing files are never overwritten.");
        await ExecuteAsync(connection, $"BACKUP DATABASE {Identifier(sourceDatabase)} TO DISK = @path WITH COPY_ONLY, CHECKSUM;",
            [new SqlParameter("@path", serverBackupPath)], cancellationToken);
        await ExecuteAsync(connection, "RESTORE VERIFYONLY FROM DISK = @path WITH CHECKSUM;",
            [new SqlParameter("@path", serverBackupPath)], cancellationToken);

        var timestamp = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var setting = await database.CustomSettings.SingleOrDefaultAsync(x => x.Key == "Backup.LastSuccessfulUtc", cancellationToken);
        if (setting is null) database.CustomSettings.Add(new CustomSetting("Backup.LastSuccessfulUtc", timestamp));
        else setting.ChangeValue(timestamp);
        database.AuditLogs.Add(new AuditLog(actorId, "BACKUP_CREATED", "Database", null, Environment.MachineName));
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task RestoreToNewDatabaseAsync(int actorId, string serverBackupPath, string targetDatabase,
        string serverDataDirectory, CancellationToken cancellationToken = default)
    {
        await authorization.DemandAsync(actorId, PermissionCodes.BackupRestore, cancellationToken);
        ValidatePath(serverBackupPath);
        ValidatePath(serverDataDirectory);
        ValidateDatabaseName(targetDatabase);
        var builder = new SqlConnectionStringBuilder(database.Database.GetConnectionString());
        if (string.Equals(builder.InitialCatalog, targetDatabase, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Restore requires a new database.", nameof(targetDatabase));
        builder.InitialCatalog = "master";
        builder.Pooling = false;
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await AcquireSessionLockAsync(connection, "HisabTak.Restore:" + targetDatabase.ToUpperInvariant(), cancellationToken);
        await using (var exists = new SqlCommand("SELECT COUNT(*) FROM sys.databases WHERE name = @name", connection))
        {
            exists.Parameters.AddWithValue("@name", targetDatabase);
            if (Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 0)
                throw new InvalidOperationException("The restore target already exists. It will not be replaced.");
        }
        await ExecuteAsync(connection, "RESTORE VERIFYONLY FROM DISK = @path WITH CHECKSUM;",
            [new SqlParameter("@path", serverBackupPath)], cancellationToken);

        var files = new List<(string Name, string Type)>();
        await using (var list = new SqlCommand("RESTORE FILELISTONLY FROM DISK = @path", connection))
        {
            list.Parameters.AddWithValue("@path", serverBackupPath);
            await using var reader = await list.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                files.Add((reader.GetString(reader.GetOrdinal("LogicalName")), reader.GetString(reader.GetOrdinal("Type"))));
        }
        if (files.Count == 0 || files.Any(x => x.Type is not ("D" or "L")))
            throw new NotSupportedException("Only ordinary SQL data and log files are supported by this restore tool.");
        var moves = new List<string>();
        var parameters = new List<SqlParameter> { new("@path", serverBackupPath) };
        var token = Guid.NewGuid().ToString("N");
        var separator = serverDataDirectory.Contains('\\') ? '\\' : '/';
        for (var index = 0; index < files.Count; index++)
        {
            var path = serverDataDirectory.TrimEnd('/', '\\') + separator + $"{targetDatabase}_{token}_{index}.{(files[index].Type == "L" ? "ldf" : "mdf")}";
            if (await FileExistsAsync(connection, path, cancellationToken)) throw new IOException("A restore file already exists.");
            moves.Add($"MOVE @logical{index} TO @physical{index}");
            parameters.Add(new SqlParameter($"@logical{index}", files[index].Name));
            parameters.Add(new SqlParameter($"@physical{index}", path));
        }
        await ExecuteAsync(connection, $"RESTORE DATABASE {Identifier(targetDatabase)} FROM DISK = @path WITH CHECKSUM, {string.Join(", ", moves)};",
            parameters, cancellationToken);
        database.AuditLogs.Add(new AuditLog(actorId, "RESTORED_NEW_DATABASE", "Database", null, Environment.MachineName));
        await database.SaveChangesAsync(cancellationToken);
    }

    private static async Task<bool> FileExistsAsync(SqlConnection connection, string path, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT file_exists FROM sys.dm_os_file_exists(@path)", connection);
        command.Parameters.AddWithValue("@path", path);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 0;
    }

    private static Task ExecuteAsync(SqlConnection connection, string sql, IEnumerable<SqlParameter> parameters, CancellationToken cancellationToken)
    {
        return RunAsync();
        async Task RunAsync()
        {
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
            command.Parameters.AddRange(parameters.ToArray());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static Task AcquireSessionLockAsync(SqlConnection connection, string resource, CancellationToken cancellationToken) =>
        ExecuteAsync(connection, """
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 10000;
            IF @result < 0 THROW 51000, 'Could not acquire recovery lock.', 1;
            """, [new SqlParameter("@resource", resource)], cancellationToken);

    private static string Identifier(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";

    private static void ValidateDatabaseName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 100 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')
            || new[] { "master", "model", "msdb", "tempdb" }.Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Use a new application database name containing letters, digits or underscores.", nameof(name));
    }

    private static void ValidatePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var windowsAbsolute = path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/';
        if (path.Length > 500 || !(Path.IsPathFullyQualified(path) || windowsAbsolute || path.StartsWith("\\\\", StringComparison.Ordinal)))
            throw new ArgumentException("Use an absolute path on the SQL Server machine.", nameof(path));
    }
}
