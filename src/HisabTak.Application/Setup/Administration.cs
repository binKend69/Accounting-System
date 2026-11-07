namespace HisabTak.Application.Setup;

public sealed class DirectoryConflictException(string message) : InvalidOperationException(message) { }
public enum DirectoryKind { Center, Department }
public enum DirectoryAction { Create, Rename, Hide, Restore }
public sealed record DirectoryRow(int Id, string Name, bool Hidden, byte[] Version)
{
    public string State => Hidden ? "مخفي" : "ظاهر";
}
public sealed record AdministrationBranch(int Id, string Label);
public interface IAdministrationAccess
{
    Task<IReadOnlyList<AdministrationBranch>> BranchesAsync(int actor);
}
public interface IReferenceDirectory
{
    Task<IReadOnlyList<DirectoryRow>> ListAsync(int actor, int branch, DirectoryKind kind);
    Task ApplyAsync(int actor, int branch, DirectoryKind kind, DirectoryAction action, DirectoryRow? original, string name);
}

public sealed record SecuritySubject(int Id, string Name);
public sealed record PermissionChoice(int Id, string Name, string Module, bool Granted, bool Inherited)
{
    public string Source => (Granted, Inherited) switch
    {
        (true, true) => "مباشرة ومن الدور", (true, false) => "مباشرة",
        (false, true) => "من الدور", _ => "غير ممنوحة"
    };
}
public sealed record RoleChoice(int Id, string Name, bool Assigned);
public sealed record UserAccess(IReadOnlyList<RoleChoice> Roles, IReadOnlyList<PermissionChoice> Permissions);
public interface ISecurityDirectory
{
    Task<IReadOnlyList<SecuritySubject>> UsersAsync(int actor);
    Task<IReadOnlyList<SecuritySubject>> RolesAsync(int actor);
    Task<IReadOnlyList<PermissionChoice>> RoleAccessAsync(int actor, int role);
    Task SetRolePermissionAsync(int actor, int role, int permission, bool granted);
    Task<UserAccess> AccessAsync(int actor, int user);
    Task SetRoleAsync(int actor, int user, int role, bool assigned);
    Task SetPermissionAsync(int actor, int user, int permission, bool granted);
}
