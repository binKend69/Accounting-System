using HisabTak.Application.Security;
using HisabTak.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HisabTak.Infrastructure.Security;

public sealed class AuthorizationService(HisabTakDbContext database) : IAuthorizationService
{
    public async Task DemandAsync(int userId, string permissionCode, CancellationToken cancellationToken = default)
    {
        var active = await database.Users.AsNoTracking().AnyAsync(x => x.Id == userId && x.IsActive, cancellationToken);
        if (!active) throw new UnauthorizedAccessException("Permission denied.");
        var permissionId = await database.Permissions.Where(x => x.Code == permissionCode && x.IsActive)
            .Select(x => (int?)x.Id).SingleOrDefaultAsync(cancellationToken);
        if (permissionId is null) throw new UnauthorizedAccessException("Permission denied.");

        var direct = await database.UserPermissions.AnyAsync(x => x.UserId == userId && x.PermissionId == permissionId, cancellationToken);
        var roleGrant = await (from ur in database.UserRoles
                               join role in database.Roles.Where(x => x.IsActive) on ur.RoleId equals role.Id
                               join grant in database.RolePermissions on role.Id equals grant.RoleId
                               where ur.UserId == userId && grant.PermissionId == permissionId
                               select grant).AnyAsync(cancellationToken);
        if (!direct && !roleGrant) throw new UnauthorizedAccessException("Permission denied.");
    }
}
