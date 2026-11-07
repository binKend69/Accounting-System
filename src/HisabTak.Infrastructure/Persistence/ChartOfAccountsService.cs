using HisabTak.Application.Accounting;
using HisabTak.Application.Security;
using HisabTak.Domain.Accounting;
using HisabTak.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace HisabTak.Infrastructure.Persistence;

public sealed class ChartOfAccountsService(HisabTakDbContext db, IAuthorizationService authorization) : IChartOfAccountsService
{
    public async Task<int> CreateGroupAsync(int actorId, string code, string name, int? parentId, CancellationToken cancellationToken = default)
    {
        var group = new AccountMainGroup(code, name, parentId);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockAsync(actorId, cancellationToken);
        await ValidateParentAsync(null, parentId, cancellationToken);
        if (await db.AccountMainGroups.IgnoreQueryFilters().AnyAsync(x => x.Code == group.Code, cancellationToken))
            throw new InvalidOperationException("Group code already exists, including archived groups.");
        db.AccountMainGroups.Add(group);
        await db.SaveChangesAsync(cancellationToken);
        await AuditAsync(actorId, "ACCOUNT_GROUP_CREATED", "AccountMainGroups", group.Id, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return group.Id;
    }

    public async Task MoveGroupAsync(int actorId, int groupId, int? parentId, CancellationToken cancellationToken = default)
    {
        if (parentId is <= 0) throw new ArgumentOutOfRangeException(nameof(parentId));
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockAsync(actorId, cancellationToken);
        var group = await db.AccountMainGroups.AsNoTracking().SingleOrDefaultAsync(x => x.Id == groupId && x.IsActive, cancellationToken)
            ?? throw new InvalidOperationException("Group is unavailable.");
        await ValidateParentAsync(group.Id, parentId, cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE dbo.AccountMainGroups SET ParentMainGroupId={parentId}, ModifiedAt=SYSUTCDATETIME() WHERE MainGroupId={groupId}", cancellationToken);
        await AuditAsync(actorId, "ACCOUNT_GROUP_MOVED", "AccountMainGroups", groupId, cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    public Task<int> CreateAccountAsync(int actorId, string code, string name, int groupId, int currencyId, byte nature, int branchId, CancellationToken cancellationToken = default) =>
        CreateClassifiedAccountAsync(actorId, branchId, code, name, groupId, currencyId, nature, null, cancellationToken);

    public async Task<int> CreateClassifiedAccountAsync(int actorId, int branchId, string code, string name, int groupId, int currencyId, byte nature, int? generalGroupId, CancellationToken cancellationToken = default)
    {
        var account = new Account(code, name, groupId, currencyId, nature);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockAsync(actorId, cancellationToken);
        await AccountingAccess.BranchAsync(db, authorization, actorId, branchId, cancellationToken);
        await ValidateParentAsync(null, groupId, cancellationToken);
        if (!await db.Currencies.AsNoTracking().AnyAsync(x => x.Id == currencyId && x.IsActive, cancellationToken))
            throw new InvalidOperationException("Currency is unavailable.");
        if (await db.Accounts.IgnoreQueryFilters().AnyAsync(x => x.AccountCode == account.AccountCode, cancellationToken))
            throw new InvalidOperationException("Account code already exists, including archived accounts.");
        if (generalGroupId is int classification && !await db.AccountGeneralGroups.AnyAsync(x => x.Id == classification && x.IsActive, cancellationToken))
            throw new InvalidOperationException("Account classification is unavailable.");
        db.Accounts.Add(account);
        db.Entry(account).Property(x => x.GeneralGroupId).CurrentValue = generalGroupId;
        db.Entry(account).Property(x => x.BranchId).CurrentValue = branchId;
        await db.SaveChangesAsync(cancellationToken);
        await AuditAsync(actorId, "ACCOUNT_CREATED", "Accounts", account.Id, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return account.Id;
    }

    public async Task SetAccountSuspendedAsync(int actorId, int accountId, bool suspended, CancellationToken cancellationToken = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockAsync(actorId, cancellationToken);
        var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == accountId, cancellationToken)
            ?? throw new InvalidOperationException("Account is unavailable.");
        if (account.BranchId is not int branch) throw new InvalidOperationException("Assign an explicit branch before managing this account.");
        await AccountingAccess.BranchAsync(db, authorization, actorId, branch, cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE dbo.Accounts SET IsSuspended={suspended}, ModifiedAt=SYSUTCDATETIME() WHERE AccountId={accountId}", cancellationToken);
        await AuditAsync(actorId, "ACCOUNT_SUSPENSION_CHANGED", "Accounts", accountId, cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    public async Task SetGeneralGroupAsync(int actorId, int accountId, int generalGroupId, CancellationToken cancellationToken = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockAsync(actorId, cancellationToken);
        var account = await db.Accounts.AsNoTracking().SingleAsync(x => x.Id == accountId, cancellationToken);
        if (account.BranchId is not int branch) throw new InvalidOperationException("Explicit branch required.");
        await AccountingAccess.BranchAsync(db, authorization, actorId, branch, cancellationToken);
        if (!await db.AccountGeneralGroups.AnyAsync(x => x.Id == generalGroupId && x.IsActive, cancellationToken)) throw new InvalidOperationException("Group unavailable.");
        if (account.GeneralGroupId != generalGroupId && await new SqlLedger(db).ScalarAsync("SELECT COUNT_BIG(*) FROM dbo.JournalLines WHERE AccountId=@id", cancellationToken, ("@id", accountId)) > 0)
            throw new InvalidOperationException("Historical account classification cannot change.");
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE dbo.Accounts SET GeneralGroupId={generalGroupId},ModifiedAt=SYSUTCDATETIME() WHERE AccountId={accountId}", cancellationToken);
        await AuditAsync(actorId, "ACCOUNT_CLASSIFIED", "Accounts", accountId, cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    private async Task LockAsync(int actorId, CancellationToken ct)
    {
        await DatabaseLock.AcquireAsync(db, "HisabTak.Accounting.Periods", ct);
        await authorization.DemandAsync(actorId, PermissionCodes.AccountManage, ct);
    }

    private async Task ValidateParentAsync(int? movingId, int? parentId, CancellationToken ct)
    {
        var seen = new HashSet<int>();
        while (parentId is int id)
        {
            if (id == movingId || !seen.Add(id)) throw new InvalidOperationException("Account group cycle is not allowed.");
            var parent = await db.AccountMainGroups.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.IsActive, ct)
                ?? throw new InvalidOperationException("Parent group is unavailable.");
            parentId = parent.ParentMainGroupId;
        }
    }

    private async Task AuditAsync(int actor, string action, string entity, int id, CancellationToken ct)
    {
        db.AuditLogs.Add(new AuditLog(actor, action, entity, id, Environment.MachineName));
        await db.SaveChangesAsync(ct);
    }
}
