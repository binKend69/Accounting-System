using Microsoft.EntityFrameworkCore;

namespace HisabTak.Infrastructure.Persistence;

internal static class DatabaseLock
{
    public static Task AcquireAsync(HisabTakDbContext database, string resource, CancellationToken cancellationToken)
    {
        if (database.Database.CurrentTransaction is null) throw new InvalidOperationException("A transaction is required for this lock.");
        return database.Database.ExecuteSqlInterpolatedAsync($"""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource = {resource},
                @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000;
            IF @result < 0 THROW 51000, 'Could not acquire the database setup lock.', 1;
            """, cancellationToken);
    }
}
