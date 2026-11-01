using HisabTak.Application.Accounting;
using HisabTak.Application.Security;
using HisabTak.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace HisabTak.Infrastructure.Persistence;

public sealed class AccountingWorkspaceService(HisabTakDbContext db, IAuthorizationService authorization) : IAccountingWorkspaceService
{
    public async Task<AccountingWorkspace> LoadAsync(int actorId, int? branchId, CancellationToken cancellationToken = default)
    {
        await authorization.DemandAsync(actorId, PermissionCodes.JournalRead, cancellationToken);
        var user = await db.Users.AsNoTracking().SingleAsync(x => x.Id == actorId && x.IsActive, cancellationToken);
        var all = false;
        try { await authorization.DemandAsync(actorId, PermissionCodes.AccountingAllBranches, cancellationToken); all = true; }
        catch (UnauthorizedAccessException) { }
        var branches = await db.Branches.AsNoTracking().Where(x => x.IsActive && (all || x.Id == user.BranchId))
            .OrderBy(x => x.Id).Select(x => new AccountingLookup(x.Id, x.NameAr)).ToListAsync(cancellationToken);
        var accounts = new List<AccountLookup>();
        if (branchId is int branch)
        {
            await AccountingAccess.BranchAsync(db, authorization, actorId, branch, cancellationToken);
            accounts = await db.Accounts.AsNoTracking().Where(x => x.BranchId == branch && x.IsActive && !x.IsSuspended)
                .OrderBy(x => x.AccountCode).Select(x => new AccountLookup(x.Id, x.AccountCode + " — " + x.NameAr, x.CurrencyId, x.GeneralGroupId)).ToListAsync(cancellationToken);
        }
        return new(branches, accounts,
            await db.Currencies.AsNoTracking().Where(x => x.IsActive).Select(x => new AccountingLookup(x.Id, x.Code + " — " + x.NameAr)).ToListAsync(cancellationToken),
            await db.AccountMainGroups.AsNoTracking().Where(x => x.IsActive).Select(x => new AccountingLookup(x.Id, x.Code + " — " + x.NameAr)).ToListAsync(cancellationToken),
            await db.AccountGeneralGroups.AsNoTracking().Where(x => x.IsActive).Select(x => new AccountingLookup(x.Id, x.NameAr)).ToListAsync(cancellationToken),
            await db.Companies.AsNoTracking().Where(x => x.IsActive).Select(x => x.BaseCurrencyId).SingleOrDefaultAsync(cancellationToken));
    }
}
