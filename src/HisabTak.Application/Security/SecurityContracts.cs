namespace HisabTak.Application.Security;

public sealed record AuthenticatedUser(int UserId, string UserName);

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string hash);
}

public interface IInitialOwnerService
{
    Task<int> CreateAsync(string username, string password, CancellationToken cancellationToken = default);
}

public interface IAuthorizationService
{
    Task DemandAsync(int userId, string permissionCode, CancellationToken cancellationToken = default);
}

public interface IAuthenticationService
{
    Task<AuthenticatedUser?> AuthenticateAsync(string username, string password, CancellationToken cancellationToken = default);
}
