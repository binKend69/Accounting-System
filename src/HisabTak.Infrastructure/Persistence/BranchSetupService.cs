using HisabTak.Application.Security;
using HisabTak.Application.Setup;
using HisabTak.Domain.Security;
using HisabTak.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace HisabTak.Infrastructure.Persistence;

public sealed class BranchSetupService(HisabTakDbContext database, IAuthorizationService authorization) : IBranchSetupService
{
    public async Task<int> CreateAsync(int actorId, string branchName, CancellationToken cancellationToken = default)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        // Serialize with first-time setup so a branch cannot precede its merchant.
        await DatabaseLock.AcquireAsync(database, "HisabTak.InitializeCompany", cancellationToken);
        await authorization.DemandAsync(actorId, PermissionCodes.SetupManage, cancellationToken);
        if (!await database.Companies.AnyAsync(x => x.IsActive, cancellationToken))
            throw new InvalidOperationException("Initialize an active company before adding branches.");

        var branch = new Branch(branchName);
        database.Branches.Add(branch);
        await database.SaveChangesAsync(cancellationToken);
        database.AuditLogs.Add(new AuditLog(actorId, "BRANCH_CREATED", "Branches", branch.Id, Environment.MachineName));
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return branch.Id;
    }
}
