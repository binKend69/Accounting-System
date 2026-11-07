using HisabTak.Application.Security;
using HisabTak.Application.Setup;
using HisabTak.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace HisabTak.Infrastructure.Persistence;

internal sealed class AdministrationAccess(HisabTakDbContext db, IAuthorizationService auth) : IAdministrationAccess
{
    public async Task<IReadOnlyList<AdministrationBranch>> BranchesAsync(int actor)
    {
        try { await auth.DemandAsync(actor, PermissionCodes.SetupManage); }
        catch (UnauthorizedAccessException) { await auth.DemandAsync(actor, PermissionCodes.UsersManage); }

        var branches = db.Branches.Where(x => x.IsActive);
        try { await auth.DemandAsync(actor, PermissionCodes.AccountingAllBranches); }
        catch (UnauthorizedAccessException)
        {
            var ownBranch = await db.Users.Where(x => x.Id == actor && x.IsActive).Select(x => x.BranchId).SingleAsync();
            branches = branches.Where(x => x.Id == ownBranch);
        }
        return await branches.OrderBy(x => x.Id).Select(x => new AdministrationBranch(x.Id, x.NameAr)).ToListAsync();
    }
}
