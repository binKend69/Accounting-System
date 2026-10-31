using HisabTak.Domain.Common;

namespace HisabTak.Domain.Security;

public sealed class User : ReferenceEntity
{
    private User() { }
    public User(string username, string passwordHash) : base(username)
    {
        UserName = RequiredText(username, 50);
        PasswordHash = RequiredText(passwordHash, 300);
        if (PasswordHash.Length != 60 || !(PasswordHash.StartsWith("$2a$", StringComparison.Ordinal)
            || PasswordHash.StartsWith("$2b$", StringComparison.Ordinal) || PasswordHash.StartsWith("$2y$", StringComparison.Ordinal)))
            throw new ArgumentException("A BCrypt hash is required.", nameof(passwordHash));
    }
    public string UserName { get; private set; } = string.Empty;
    public string PasswordHash { get; private set; } = string.Empty;
    public int? BranchId { get; private set; }
    public int? CenterId { get; private set; }
    public int? LinkedAccountId { get; private set; }
    public bool IsCashierAccount { get; private set; }
    public DateTime? LastLoginAt { get; private set; }
    public void RecordLogin(DateTime utcNow) => LastLoginAt = utcNow;
}

public sealed class Role : ReferenceEntity
{
    private Role() { }
    public Role(string code, string name, bool systemRole = false) : base(name)
    {
        Code = RequiredText(code, 30);
        IsSystemRole = systemRole;
    }
    public string Code { get; private set; } = string.Empty;
    public bool IsSystemRole { get; private set; }
}

public sealed class Permission : ReferenceEntity
{
    private Permission() { }
    public Permission(string code, string name, string module, int? parentId = null) : base(name)
    {
        Code = RequiredText(code, 100);
        Module = RequiredText(module, 50);
        ParentPermissionId = parentId;
    }
    public string Code { get; private set; } = string.Empty;
    public string Module { get; private set; } = string.Empty;
    public int? ParentPermissionId { get; private set; }
    public int DisplayOrder { get; private set; }
}

public sealed class UserRole
{
    private UserRole() { }
    public UserRole(int userId, int roleId) { UserId = userId; RoleId = roleId; }
    public int UserId { get; private set; }
    public int RoleId { get; private set; }
}

public sealed class RolePermission
{
    private RolePermission() { }
    public RolePermission(int roleId, int permissionId) { RoleId = roleId; PermissionId = permissionId; }
    public int RoleId { get; private set; }
    public int PermissionId { get; private set; }
}

public sealed class UserPermission
{
    private UserPermission() { }
    public UserPermission(int userId, int permissionId, int? grantedByUserId = null)
    {
        UserId = userId; PermissionId = permissionId; GrantedByUserId = grantedByUserId;
    }
    public int UserId { get; private set; }
    public int PermissionId { get; private set; }
    public DateTime GrantedAt { get; private set; }
    public int? GrantedByUserId { get; private set; }
}

public sealed class AuditLog
{
    private AuditLog() { }
    public AuditLog(int? userId, string operation, string entityType, long? recordId, string computerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        UserId = userId; OperationType = operation; DocumentTypeCode = entityType;
        RecordId = recordId; ComputerName = computerName;
    }
    public long AuditLogId { get; private set; }
    public int? UserId { get; private set; }
    public DateOnly ActionDate { get; private set; }
    public TimeOnly ActionTime { get; private set; }
    public string? DocumentTypeCode { get; private set; }
    public string OperationType { get; private set; } = string.Empty;
    public string? WindowName { get; private set; }
    public string? ComputerName { get; private set; }
    public string? Details { get; private set; }
    public long? RecordId { get; private set; }
    public DateTime CreatedAt { get; private set; }
}

public static class PermissionCodes
{
    public const string AccountManage = "ACCOUNTING.ACCOUNT.MANAGE";
    public const string AccountingAllBranches = "ACCOUNTING.BRANCH.ALL";
    public const string JournalPost = "ACCOUNTING.JOURNAL.POST";
    public const string JournalReverse = "ACCOUNTING.JOURNAL.REVERSE";
    public const string JournalRead = "ACCOUNTING.JOURNAL.READ";
    public const string SetupManage = "SETUP.MANAGE";
    public const string UsersManage = "SECURITY.MANAGE";
    public const string AuditRead = "AUDIT.VIEW";
    public const string BackupCreate = "BACKUP.CREATE";
    public const string BackupRestore = "BACKUP.RESTORE";
    public const string PeriodClose = "ACCOUNTING.PERIOD.CLOSE";
    public const string PeriodReopen = "ACCOUNTING.PERIOD.REOPEN";
}
