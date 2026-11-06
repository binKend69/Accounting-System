using MotoParts.Core.Common;

namespace MotoParts.Core.Security;

/// <summary>
/// The person performing an action: who they are and what they may do.
///
/// Passed explicitly to every service method that changes something, so each
/// write is attributable and each privileged action is checked where it
/// happens, not only by which buttons the screen chose to show. Hiding a
/// button is not access control.
/// </summary>
public sealed class Actor
{
    public Actor(long id, string username, string fullName, IEnumerable<string> roles, IEnumerable<string> permissions)
    {
        Id = id;
        Username = username;
        FullName = fullName;
        Roles = roles.ToHashSet(StringComparer.Ordinal);
        Permissions = permissions.ToHashSet(StringComparer.Ordinal);
    }

    public long Id { get; }

    public string Username { get; }

    public string FullName { get; }

    public IReadOnlySet<string> Roles { get; }

    public IReadOnlySet<string> Permissions { get; }

    public bool IsOwner => Roles.Contains(RoleName.Owner);

    public bool Can(string permission) => Permissions.Contains(permission);

    public bool CanAny(params string[] permissions) => permissions.Any(Permissions.Contains);

    public void Require(string permission, string? message = null)
    {
        if (!Can(permission))
        {
            throw new AccessDeniedException(message ?? "ليست لديك صلاحية لتنفيذ هذا الإجراء.");
        }
    }

    public void RequireOwner(string message)
    {
        if (!IsOwner)
        {
            throw new AccessDeniedException(message);
        }
    }
}
