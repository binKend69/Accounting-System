using HisabTak.Application.Security;
using HisabTak.Application.Setup;
using HisabTak.Domain.Security;
using HisabTak.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace HisabTak.Infrastructure.Persistence;

public sealed class CompanySetupService(HisabTakDbContext database, IAuthorizationService authorization) : ICompanySetupService
{
    public async Task InitializeAsync(int actorId, string companyName, string branchName, CancellationToken cancellationToken = default)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await DatabaseLock.AcquireAsync(database, "HisabTak.InitializeCompany", cancellationToken);
        await authorization.DemandAsync(actorId, PermissionCodes.SetupManage, cancellationToken);
        if (await database.Companies.IgnoreQueryFilters().AnyAsync(cancellationToken)
            || await database.Branches.IgnoreQueryFilters().AnyAsync(cancellationToken))
            throw new InvalidOperationException("Company setup has already been completed.");

        var branch = new Branch(branchName);
        database.Branches.Add(branch);
        await database.SaveChangesAsync(cancellationToken);
        database.Companies.Add(new CompanyInfo(companyName, branch.Id));
        database.AuditLogs.Add(new AuditLog(actorId, "COMPANY_INITIALIZED", "CompanyInfo", 1, Environment.MachineName));
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
