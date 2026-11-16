using HisabTak.Application.Accounting;
using HisabTak.Application.Setup;
using HisabTak.Domain.Accounting;
using HisabTak.Domain.Security;
using Microsoft.EntityFrameworkCore;
namespace HisabTak.Infrastructure.Tests;
public sealed partial class LedgerIntegrationTests
{
    [SqlServerFact]
    public async Task Trial_balance_separates_opening_activity_reversal_and_future_dates()
    {
        await using var test=await SqlTestDatabase.CreateAsync();var f=await SetupAsync(test);
        await Post(test,f,f.Draft(opening:true));var id=await Post(test,f,f.Draft(amount:40));
        Task<IReadOnlyList<TrialBalanceRow>> Report(DateOnly to)=>With<IFinancialReports,IReadOnlyList<TrialBalanceRow>>(test,s=>s.TrialBalanceAsync(f.Owner,f.Branch,null,Date,to));
        var before=await Report(Date);var row=before.Single(r=>r.AccountId==f.Debit);
        Assert.Equal(100,row.OpeningDebit);Assert.Equal(40,row.PeriodDebit);Assert.Equal(140,row.ClosingDebit);
        await With<IJournalService,long>(test,s=>s.ReverseAsync(f.Owner,id,Date.AddDays(1),Guid.NewGuid()));
        Assert.Equal(before,await Report(Date));
        var after=await Report(Date.AddDays(1));row=after.Single(r=>r.AccountId==f.Debit);
        Assert.Equal(40,row.PeriodCredit);Assert.Equal(100,row.ClosingDebit);
        Assert.Equal(after.Sum(r=>r.ClosingDebit),after.Sum(r=>r.ClosingCredit));
        await Change<IChartOfAccountsService>(test,s=>s.SetAccountSuspendedAsync(f.Owner,f.Debit,true));
        Assert.Equal(after,await Report(Date.AddDays(1)));
    }
    [SqlServerFact]
    public async Task Trial_balance_keeps_currency_branch_and_permission_boundaries()
    {
        await using var test=await SqlTestDatabase.CreateAsync();var f=await SetupAsync(test);
        var foreign=await With<IChartOfAccountsService,int>(test,s=>s.CreateAccountAsync(f.Owner,"009003","Foreign",f.Group,f.OtherCurrency,1,f.Branch));
        await Post(test,f,JournalDraft.Create(f.Branch,new FiscalPeriod("proposal",Date,Date),Date,Guid.NewGuid(),[
            new(foreign,JournalSide.Debit,new(new(10,f.OtherCurrency),f.Currency,3.8m,Date)),
            new(f.Credit,JournalSide.Credit,new(new(38,f.Currency),f.Currency,1,Date))]));
        Task<IReadOnlyList<TrialBalanceRow>> Report(int actor,int branch,int? currency)=>With<IFinancialReports,IReadOnlyList<TrialBalanceRow>>(test,s=>s.TrialBalanceAsync(actor,branch,currency,Date,Date));
        var original=await Report(f.Owner,f.Branch,f.OtherCurrency);Assert.Equal(10,Assert.Single(original).ClosingDebit);
        var all=await Report(f.Owner,f.Branch,null);Assert.Equal(38,all.Sum(r=>r.ClosingDebit));Assert.Equal(38,all.Sum(r=>r.ClosingCredit));
        await using(var db=test.Context())await db.Database.ExecuteSqlRawAsync("UPDATE dbo.Currencies SET ExchangeRate=99 WHERE CurrencyId={0}",f.OtherCurrency);
        await Post(test,f,f.Draft(amount:50));Assert.Equal(original,await Report(f.Owner,f.Branch,f.OtherCurrency));
        var other=await With<IBranchSetupService,int>(test,s=>s.CreateAsync(f.Owner,"Another report branch"));Assert.Empty(await Report(f.Owner,other,null));
        var d2=await With<IChartOfAccountsService,int>(test,s=>s.CreateAccountAsync(f.Owner,"008001","Other debit",f.Group,f.Currency,1,other));
        var c2=await With<IChartOfAccountsService,int>(test,s=>s.CreateAccountAsync(f.Owner,"008002","Other credit",f.Group,f.Currency,2,other));
        var rounding=await With<IChartOfAccountsService,int>(test,s=>s.CreateAccountAsync(f.Owner,"008099","Other rounding",f.Group,f.Currency,1,other));
        await Change<IJournalService>(test,s=>s.ConfigureRoundingAsync(f.Owner,other,rounding,2));
        var snapshot=await Report(f.Owner,f.Branch,null);
        await Post(test,f,JournalDraft.Create(other,new FiscalPeriod("proposal",Date,Date),Date,Guid.NewGuid(),[
            new(d2,JournalSide.Debit,new(new(500,f.Currency),f.Currency,1,Date)),new(c2,JournalSide.Credit,new(new(500,f.Currency),f.Currency,1,Date))]));
        Assert.Equal(snapshot,await Report(f.Owner,f.Branch,null));
        int reader;
        await using(var db=test.Context())
        {
            var u=new User("report-reader",new HisabTak.Infrastructure.Security.BCryptPasswordHasher().Hash("Test-"+Guid.NewGuid().ToString("N")));
            db.Users.Add(u);await db.SaveChangesAsync();reader=u.Id;
            db.UserPermissions.Add(new UserPermission(reader,(await db.Permissions.SingleAsync(p=>p.Code==PermissionCodes.JournalRead)).Id));await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("UPDATE dbo.Users SET BranchId={0} WHERE UserId={1}",f.Branch,reader);
        }
        Assert.NotEmpty(await Report(reader,f.Branch,null));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>Report(reader,other,null));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>Report(0,f.Branch,null));
        await Assert.ThrowsAsync<ArgumentException>(()=>With<IFinancialReports,IReadOnlyList<TrialBalanceRow>>(test,s=>s.TrialBalanceAsync(f.Owner,f.Branch,null,Date.AddDays(1),Date)));
    }
}
