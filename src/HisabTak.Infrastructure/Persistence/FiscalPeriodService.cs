using HisabTak.Application.Accounting;
using HisabTak.Application.Security;
using HisabTak.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace HisabTak.Infrastructure.Persistence;

public sealed class FiscalPeriodService(HisabTakDbContext database, IAuthorizationService authorization) : IFiscalPeriodService
{
    public Task CloseAsync(int actorId, int periodId, CancellationToken cancellationToken = default) =>
        ChangeStateAsync(actorId, periodId, close: true, cancellationToken);

    public Task ReopenAsync(int actorId, int periodId, CancellationToken cancellationToken = default) =>
        ChangeStateAsync(actorId, periodId, close: false, cancellationToken);

    private async Task ChangeStateAsync(int actorId, int periodId, bool close, CancellationToken cancellationToken)
    {
        if (periodId <= 0) throw new ArgumentOutOfRangeException(nameof(periodId));
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        // Future journal posting must acquire this same transaction-owned lock.
        await DatabaseLock.AcquireAsync(database, "HisabTak.Accounting.Periods", cancellationToken);
        await authorization.DemandAsync(actorId, close ? PermissionCodes.PeriodClose : PermissionCodes.PeriodReopen, cancellationToken);
        var period = await database.FiscalPeriods.SingleOrDefaultAsync(x => x.Id == periodId, cancellationToken)
            ?? throw new InvalidOperationException("Fiscal period was not found.");
        // A scoped context may already track the period; refresh after acquiring the SQL lock.
        await database.Entry(period).ReloadAsync(cancellationToken);
        if (close) period.Close(actorId, DateTime.UtcNow);
        else period.Reopen();
        database.AuditLogs.Add(new AuditLog(actorId, close ? "FISCAL_PERIOD_CLOSED" : "FISCAL_PERIOD_REOPENED",
            "FiscalPeriods", period.Id, Environment.MachineName));
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
