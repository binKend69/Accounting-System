using HisabTak.Application.Security;
using HisabTak.Application.Setup;
using HisabTak.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace HisabTak.Infrastructure.Persistence;

internal sealed class SecurityDirectory(HisabTakDbContext db, IAuthorizationService auth) : ISecurityDirectory
{
    private async Task<IQueryable<User>> VisibleUsers(int actor)
    {
        await auth.DemandAsync(actor, PermissionCodes.UsersManage);
        var branch = await db.Users.Where(x => x.Id == actor).Select(x => x.BranchId).SingleAsync();
        try { await auth.DemandAsync(actor, PermissionCodes.AccountingAllBranches); return db.Users.Where(x => x.IsActive); }
        catch (UnauthorizedAccessException) { return db.Users.Where(x => x.IsActive && branch != null && x.BranchId == branch); }
    }

    public async Task<IReadOnlyList<SecuritySubject>> UsersAsync(int actor) =>
        await (await VisibleUsers(actor)).OrderBy(x => x.UserName).Select(x => new SecuritySubject(x.Id, x.UserName)).ToListAsync();

    private async Task DemandUser(int actor, int user)
    {
        if (!await (await VisibleUsers(actor)).AnyAsync(x => x.Id == user)) throw new UnauthorizedAccessException();
    }

    private IQueryable<int> RolesOf(int user) =>
        from membership in db.UserRoles join role in db.Roles on membership.RoleId equals role.Id
        where membership.UserId == user && role.IsActive select role.Id;

    private async Task DemandRoleAdmin(int actor)
    {
        await auth.DemandAsync(actor, PermissionCodes.UsersManage);
        // Role definitions apply throughout the company.
        await auth.DemandAsync(actor, PermissionCodes.AccountingAllBranches);
    }

    public async Task<IReadOnlyList<SecuritySubject>> RolesAsync(int actor)
    {
        await DemandRoleAdmin(actor);
        return await db.Roles.Where(x => x.IsActive).OrderBy(x => x.NameAr)
            .Select(x => new SecuritySubject(x.Id, x.NameAr)).ToListAsync();
    }

    public async Task<IReadOnlyList<PermissionChoice>> RoleAccessAsync(int actor, int role)
    {
        await DemandRoleAdmin(actor);
        if (!await db.Roles.AnyAsync(x => x.Id == role && x.IsActive)) throw new UnauthorizedAccessException();
        return await db.Permissions.Where(x => x.IsActive && x.ParentPermissionId != null)
            .OrderBy(x => x.Module).ThenBy(x => x.NameAr).Select(x => new PermissionChoice(x.Id, x.NameAr, x.Module,
                db.RolePermissions.Any(r => r.RoleId == role && r.PermissionId == x.Id), false)).ToListAsync();
    }

    public async Task SetRolePermissionAsync(int actor, int role, int permission, bool granted)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        await DatabaseLock.AcquireAsync(db, "HisabTak.SecurityDirectory", default);
        await DemandRoleAdmin(actor);
        if (!await db.Roles.AnyAsync(x => x.Id == role && x.IsActive && x.Code != "OWNER"))
            throw new UnauthorizedAccessException();
        var code = await db.Permissions.Where(x => x.Id == permission && x.IsActive && x.ParentPermissionId != null)
            .Select(x => x.Code).SingleOrDefaultAsync() ?? throw new UnauthorizedAccessException();
        await auth.DemandAsync(actor, code);
        var existing = await db.RolePermissions.SingleOrDefaultAsync(x => x.RoleId == role && x.PermissionId == permission);
        if ((existing != null) != granted)
        {
            if (granted) db.RolePermissions.Add(new RolePermission(role, permission));
            else db.RolePermissions.Remove(existing!);
            db.AuditLogs.Add(new AuditLog(actor, granted ? "ROLE_PERMISSION_GRANT" : "ROLE_PERMISSION_REVOKE",
                $"Role:{role}", permission, Environment.MachineName));
            await db.SaveChangesAsync();
        }
        await tx.CommitAsync();
    }

    public async Task<UserAccess> AccessAsync(int actor, int user)
    {
        await DemandUser(actor, user);
        var roles = RolesOf(user);
        var memberships = await db.Roles.Where(x => x.IsActive).OrderBy(x => x.NameAr)
            .Select(x => new RoleChoice(x.Id, x.NameAr, roles.Contains(x.Id))).ToListAsync();
        var permissions = await db.Permissions.Where(x => x.IsActive && x.ParentPermissionId != null)
            .OrderBy(x => x.Module).ThenBy(x => x.NameAr).Select(x => new PermissionChoice(x.Id, x.NameAr, x.Module,
                db.UserPermissions.Any(p => p.UserId == user && p.PermissionId == x.Id),
                db.RolePermissions.Any(p => roles.Contains(p.RoleId) && p.PermissionId == x.Id))).ToListAsync();
        return new(memberships, permissions);
    }

    private async Task Write(int actor, int user, string operation, int target, Func<Task<bool>> change)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        await DatabaseLock.AcquireAsync(db, "HisabTak.SecurityDirectory", default);
        await DemandUser(actor, user);
        var roles = RolesOf(user);
        if (actor == user || await db.Roles.AnyAsync(x => x.Code == "OWNER" && roles.Contains(x.Id)))
            throw new UnauthorizedAccessException();
        if (await change())
        {
            db.AuditLogs.Add(new AuditLog(actor, operation, $"User:{user}", target, Environment.MachineName));
            await db.SaveChangesAsync();
        }
        await tx.CommitAsync();
    }

    public Task SetPermissionAsync(int actor, int user, int permission, bool granted) =>
        Write(actor, user, granted ? "PERMISSION_GRANT" : "PERMISSION_REVOKE", permission, async () =>
        {
            var code = await db.Permissions.Where(x => x.Id == permission && x.IsActive && x.ParentPermissionId != null)
                .Select(x => x.Code).SingleOrDefaultAsync() ?? throw new UnauthorizedAccessException();
            await auth.DemandAsync(actor, code);
            var existing = await db.UserPermissions.SingleOrDefaultAsync(x => x.UserId == user && x.PermissionId == permission);
            if ((existing != null) == granted) return false;
            if (granted) db.UserPermissions.Add(new UserPermission(user, permission, actor));
            else db.UserPermissions.Remove(existing!);
            return true;
        });

    public Task SetRoleAsync(int actor, int user, int role, bool assigned) =>
        Write(actor, user, assigned ? "ROLE_ASSIGN" : "ROLE_REMOVE", role, async () =>
        {
            if (!await db.Roles.AnyAsync(x => x.Id == role && x.IsActive && x.Code != "OWNER"))
                throw new UnauthorizedAccessException();
            var codes = await (from grant in db.RolePermissions join p in db.Permissions on grant.PermissionId equals p.Id
                               where grant.RoleId == role select p.Code).ToListAsync();
            foreach (var code in codes) await auth.DemandAsync(actor, code);
            var existing = await db.UserRoles.SingleOrDefaultAsync(x => x.UserId == user && x.RoleId == role);
            if ((existing != null) == assigned) return false;
            if (assigned) db.UserRoles.Add(new UserRole(user, role));
            else db.UserRoles.Remove(existing!);
            return true;
        });
}
