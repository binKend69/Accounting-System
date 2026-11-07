namespace HisabTak.Application.Backup;

public interface IDatabaseBackupService
{
    Task CreateAsync(int actorId, string serverBackupPath, CancellationToken cancellationToken = default);
    Task RestoreToNewDatabaseAsync(int actorId, string serverBackupPath, string targetDatabase,
        string serverDataDirectory, CancellationToken cancellationToken = default);
}
