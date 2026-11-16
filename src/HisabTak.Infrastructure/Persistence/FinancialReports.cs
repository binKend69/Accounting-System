using System.Data;
using Dapper;
using HisabTak.Application.Accounting;
using HisabTak.Application.Security;
using HisabTak.Domain.Security;
using Microsoft.EntityFrameworkCore;
namespace HisabTak.Infrastructure.Persistence;
public sealed class FinancialReports(HisabTakDbContext db,IAuthorizationService authorization) : IFinancialReports
{
    public async Task<IReadOnlyList<TrialBalanceRow>> TrialBalanceAsync(int actor,int branch,int? currency,DateOnly from,DateOnly to,CancellationToken ct=default)
    {
        await authorization.DemandAsync(actor,PermissionCodes.JournalRead,ct);
        await AccountingAccess.BranchAsync(db,authorization,actor,branch,ct);
        if(from>to || currency is <=0)throw new ArgumentException("Invalid report range or currency.");
        if(currency.HasValue && !await db.Currencies.AnyAsync(c=>c.Id==currency,ct))throw new ArgumentException("Unknown currency.");
        await db.Database.OpenConnectionAsync(ct);
        var command=new CommandDefinition("dbo.ReportTrialBalance",new {BranchId=branch,CurrencyId=currency,
            FromDate=from.ToDateTime(TimeOnly.MinValue),ToDate=to.ToDateTime(TimeOnly.MinValue)},commandType:CommandType.StoredProcedure,cancellationToken:ct);
        return (await db.Database.GetDbConnection().QueryAsync<TrialBalanceRow>(command)).ToArray();
    }
}
