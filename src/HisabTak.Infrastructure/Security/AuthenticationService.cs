using HisabTak.Application.Security;
using HisabTak.Domain.Security;
using HisabTak.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HisabTak.Infrastructure.Security;

public sealed class AuthenticationService(HisabTakDbContext database, IPasswordHasher passwords) : IAuthenticationService
{
    // Equalize the expensive verification path for an unknown user; this is not a login credential.
    private static readonly string DummyHash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString("N"), workFactor: 12);

    public async Task<AuthenticatedUser?> AuthenticateAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        var user = await database.Users.SingleOrDefaultAsync(x => x.UserName == username.Trim(), cancellationToken);
        var valid = passwords.Verify(password, user?.PasswordHash ?? DummyHash);
        if (user is null || !user.IsActive || !valid) return null;
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        user.RecordLogin(DateTime.UtcNow);
        database.AuditLogs.Add(new AuditLog(user.Id, "LOGIN", "User", user.Id, Environment.MachineName));
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AuthenticatedUser(user.Id, user.UserName);
    }
}
