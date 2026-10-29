using HisabTak.Application.Security;
using HisabTak.Domain.Security;
using HisabTak.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HisabTak.Infrastructure.Security;

public sealed class InitialOwnerService(HisabTakDbContext database, IPasswordHasher passwords) : IInitialOwnerService
{
    public async Task<int> CreateAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        var hash = passwords.Hash(password);
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await DatabaseLock.AcquireAsync(database, "HisabTak.InitializeOwner", cancellationToken);
        if (await database.Users.IgnoreQueryFilters().AnyAsync(cancellationToken))
            throw new InvalidOperationException("Initial owner setup has already been completed.");

        var role = await database.Roles.SingleAsync(x => x.Code == "OWNER" && x.IsActive, cancellationToken);
        var user = new User(username, hash);
        database.Users.Add(user);
        await database.SaveChangesAsync(cancellationToken);
        database.UserRoles.Add(new UserRole(user.Id, role.Id));
        database.AuditLogs.Add(new AuditLog(user.Id, "OWNER_INITIALIZED", "User", user.Id, Environment.MachineName));
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return user.Id;
    }
}
