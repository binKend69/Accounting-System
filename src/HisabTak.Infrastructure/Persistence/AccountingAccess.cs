using HisabTak.Application.Security;
using HisabTak.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace HisabTak.Infrastructure.Persistence;

internal static class AccountingAccess
{
    public static async Task BranchAsync(HisabTakDbContext db, IAuthorizationService authorization,
        int actorId, int branchId, CancellationToken ct)
    {
        if (!await db.Branches.AsNoTracking().AnyAsync(x => x.Id == branchId && x.IsActive, ct))
            throw new InvalidOperationException("Branch is unavailable.");
        var actor = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == actorId && x.IsActive, ct)
            ?? throw new UnauthorizedAccessException("User is unavailable.");
        if (actor.BranchId != branchId)
            await authorization.DemandAsync(actorId, PermissionCodes.AccountingAllBranches, ct);
    }
}
