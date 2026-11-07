using HisabTak.Application.Security;
using HisabTak.Application.Setup;
using HisabTak.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace HisabTak.Infrastructure.Tests;

public sealed partial class LedgerIntegrationTests
{
    [SqlServerFact]
    public async Task Reference_names_stay_reserved_when_hidden_and_centers_cannot_be_edited_from_another_branch()
    {
        await using var test = await SqlTestDatabase.CreateAsync();
        var f = await SetupAsync(test);
        var other = await With<IBranchSetupService, int>(test, s => s.CreateAsync(f.Owner, "Second"));
        Task Edit(int branch, DirectoryAction action, DirectoryRow? row, string name) =>
            Change<IReferenceDirectory>(test, s => s.ApplyAsync(f.Owner, branch, DirectoryKind.Center, action, row, name));
        Task<IReadOnlyList<DirectoryRow>> Read(int branch) => With<IReferenceDirectory, IReadOnlyList<DirectoryRow>>(
            test, s => s.ListAsync(f.Owner, branch, DirectoryKind.Center));

        await Edit(f.Branch, DirectoryAction.Create, null, "  North  ");
        var first = Assert.Single(await Read(f.Branch));
        Assert.Equal("North", first.Name);
        Assert.Empty(await Read(other));
        await Assert.ThrowsAsync<DirectoryConflictException>(() => Edit(other, DirectoryAction.Hide, first, ""));
        await Edit(other, DirectoryAction.Create, null, "North");
        await Edit(f.Branch, DirectoryAction.Hide, first, "");
        await Assert.ThrowsAsync<DirectoryConflictException>(() => Edit(f.Branch, DirectoryAction.Create, null, "North"));
        await Assert.ThrowsAsync<DirectoryConflictException>(() => Edit(f.Branch, DirectoryAction.Restore, first, ""));
        var hidden = Assert.Single(await Read(f.Branch));
        Assert.True(hidden.Hidden);
        await Edit(f.Branch, DirectoryAction.Restore, hidden, "");
        var restored = Assert.Single(await Read(f.Branch));
        Assert.Equal(first.Id, restored.Id);
        Assert.False(restored.Hidden);
        await using var db = test.Context();
        Assert.Equal(3, await db.AuditLogs.CountAsync(x => x.DocumentTypeCode == "Center" && x.RecordId == first.Id));
    }

    [SqlServerFact]
    public async Task Security_manager_cannot_grant_missing_rights_change_an_owner_or_administer_another_branch()
    {
        await using var test = await SqlTestDatabase.CreateAsync();
        var f = await SetupAsync(test);
        var other = await With<IBranchSetupService, int>(test, s => s.CreateAsync(f.Owner, "Second"));
        await using var db = test.Context();
        var hash = await With<IPasswordHasher, string>(test, s => Task.FromResult(s.Hash(Guid.NewGuid().ToString("N"))));
        var manager = new User("manager", hash);
        var local = new User("local", hash);
        var remote = new User("remote", hash);
        db.AddRange(manager, local, remote);
        db.Entry(manager).Property(x => x.BranchId).CurrentValue = f.Branch;
        db.Entry(local).Property(x => x.BranchId).CurrentValue = f.Branch;
        db.Entry(remote).Property(x => x.BranchId).CurrentValue = other;
        await db.SaveChangesAsync();
        var manage = await db.Permissions.SingleAsync(x => x.Code == PermissionCodes.UsersManage);
        var setup = await db.Permissions.SingleAsync(x => x.Code == PermissionCodes.SetupManage);
        var ownerRole = await db.Roles.SingleAsync(x => x.Code == "OWNER");
        var role = await db.Roles.SingleAsync(x => x.Code == "ACCOUNTANT");
        db.UserPermissions.Add(new UserPermission(manager.Id, manage.Id));
        db.RolePermissions.Add(new RolePermission(role.Id, setup.Id));
        await db.SaveChangesAsync();
        Task Set(Func<ISecurityDirectory, Task> change) => Change<ISecurityDirectory>(test, change);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Set(s => s.SetPermissionAsync(manager.Id, local.Id, setup.Id, true)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Set(s => s.SetRoleAsync(manager.Id, local.Id, role.Id, true)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Set(s => s.SetPermissionAsync(manager.Id, remote.Id, manage.Id, true)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Set(s => s.SetRoleAsync(f.Owner, local.Id, ownerRole.Id, true)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Set(s => s.SetRolePermissionAsync(f.Owner, ownerRole.Id, manage.Id, false)));
        Assert.False(await db.UserPermissions.AnyAsync(x => x.UserId == local.Id || x.UserId == remote.Id));
        Assert.False(await db.UserRoles.AnyAsync(x => x.UserId == local.Id));
        Assert.True(await db.RolePermissions.AnyAsync(x => x.RoleId == ownerRole.Id && x.PermissionId == manage.Id));
    }
}
