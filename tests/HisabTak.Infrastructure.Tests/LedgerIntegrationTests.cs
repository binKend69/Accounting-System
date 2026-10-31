using HisabTak.Application.Accounting;
using HisabTak.Application.Security;
using HisabTak.Application.Setup;
using HisabTak.Domain.Accounting;
using HisabTak.Domain.Security;
using HisabTak.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HisabTak.Infrastructure.Tests;

[Trait("Category", "SqlServer")]
public sealed class LedgerIntegrationTests
{
    private static readonly DateOnly Date = new(2026, 6, 1);
    private sealed record Fixture(int Owner, int Branch, int Currency, int OtherCurrency, int Group, int Debit, int Credit, int Period)
    {
        public JournalDraft Draft(Guid? source = null, decimal amount = 100m, bool opening = false, int? currency = null, int? branch = null)
        {
            var valuation = new BaseCurrencyValuation(new CurrencyAmount(amount, currency ?? Currency), Currency, 1m, Date);
            JournalLineDefinition[] lines = [new(Debit, JournalSide.Debit, valuation), new(Credit, JournalSide.Credit, valuation)];
            var period = new FiscalPeriod("proposal", Date, Date);
            return opening ? JournalDraft.CreateOpening(branch ?? Branch, period, Date, source ?? Guid.NewGuid(), lines)
                : JournalDraft.Create(branch ?? Branch, period, Date, source ?? Guid.NewGuid(), lines);
        }
    }

    private static async Task<Fixture> SetupAsync(SqlTestDatabase test)
    {
        await using (var db = test.Context()) await new ReferenceDataSeeder(db).SeedAsync();
        await using var services = test.Services();
        await using var scope = services.CreateAsyncScope();
        var owner = await scope.ServiceProvider.GetRequiredService<IInitialOwnerService>().CreateAsync("owner", "Test-" + Guid.NewGuid().ToString("N"));
        await scope.ServiceProvider.GetRequiredService<ICompanySetupService>().InitializeAsync(owner, "Ledger merchant", "First branch");
        var dbContext = scope.ServiceProvider.GetRequiredService<HisabTakDbContext>();
        var branch = (await dbContext.Branches.SingleAsync()).Id;
        var currency = (await dbContext.Currencies.SingleAsync(x => x.Code == "YER")).Id;
        var otherCurrency = (await dbContext.Currencies.SingleAsync(x => x.Code == "SAR")).Id;
        var period = new FiscalPeriod("2026", new(2026, 1, 1), new(2026, 12, 31));
        dbContext.FiscalPeriods.Add(period);
        await dbContext.SaveChangesAsync();
        var chart = scope.ServiceProvider.GetRequiredService<IChartOfAccountsService>();
        var group = await chart.CreateGroupAsync(owner, "009", "Test root", null);
        var debit = await chart.CreateAccountAsync(owner, "009001", "Debit", group, currency, 1, branch);
        var credit = await chart.CreateAccountAsync(owner, "009002", "Credit", group, currency, 2, branch);
        await scope.ServiceProvider.GetRequiredService<IJournalService>().SetBaseCurrencyAsync(owner, currency);
        return new(owner, branch, currency, otherCurrency, group, debit, credit, period.Id);
    }

    private static async Task<T> With<TService, T>(SqlTestDatabase test, Func<TService, Task<T>> action) where TService : notnull
    {
        await using var services = test.Services();
        await using var scope = services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }
    private static Task Change<TService>(SqlTestDatabase test, Func<TService, Task> action) where TService : notnull =>
        With<TService, bool>(test, async service => { await action(service); return true; });
    private static Task<long> Post(SqlTestDatabase test, Fixture f, JournalDraft draft) => With<IJournalService, long>(test, s => s.PostAsync(f.Owner, draft));
    private static async Task<long> Count(SqlTestDatabase test, string table)
    {
        await using var db = test.Context();
        // Table names are test-owned constants, never user input.
        var query = table switch
        {
            "JournalEntries" => "SELECT COUNT_BIG(*) AS Value FROM dbo.JournalEntries",
            "JournalLines" => "SELECT COUNT_BIG(*) AS Value FROM dbo.JournalLines",
            "JournalOpeningAccounts" => "SELECT COUNT_BIG(*) AS Value FROM dbo.JournalOpeningAccounts",
            _ => throw new ArgumentOutOfRangeException(nameof(table))
        };
        return await db.Database.SqlQueryRaw<long>(query).SingleAsync();
    }

    [SqlServerFact]
    public async Task Post_retry_and_currency_scoped_balances_have_one_effect()
    {
        await using var test = await SqlTestDatabase.CreateAsync(); var f = await SetupAsync(test);
        var draft = f.Draft(); var id = await Post(test, f, draft);
        Assert.Equal(id, await Post(test, f, draft));
        Assert.Equal(id, await Post(test, f, f.Draft(draft.SourceKey, 100.000m)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Post(test, f, f.Draft(draft.SourceKey, 101m)));
        var stored = await With<IJournalService, PostedJournal>(test, s => s.GetAsync(f.Owner, id));
        Assert.Equal(f.Period, stored.PeriodId); Assert.Equal(2, stored.Lines.Count);
        Assert.All(stored.Lines, l => Assert.Equal(100m, l.BaseAmount));
        var balances = await With<IJournalService, IReadOnlyList<LedgerBalance>>(test, s => s.BalancesAsync(f.Owner, f.Branch, f.Currency, Date));
        Assert.Equal(100m, balances.Sum(x => x.Debit)); Assert.Equal(100m, balances.Sum(x => x.Credit));
        Assert.Empty(await With<IJournalService, IReadOnlyList<LedgerBalance>>(test, s => s.BalancesAsync(f.Owner, f.Branch, f.OtherCurrency, Date)));
        Assert.Equal(1, await Count(test, "JournalEntries"));
        await using var db = test.Context();
        Assert.Single(await db.AuditLogs.Where(x => x.OperationType == "JOURNAL_POSTED").ToListAsync());
    }

    [SqlServerFact]
    public async Task Concurrent_retries_return_the_same_committed_journal()
    {
        await using var test = await SqlTestDatabase.CreateAsync(); var f = await SetupAsync(test); var draft = f.Draft();
        var ids = await Task.WhenAll(Post(test, f, draft), Post(test, f, draft));
        Assert.Equal(ids[0], ids[1]); Assert.Equal(1, await Count(test, "JournalEntries"));
    }

    [SqlServerFact]
    public async Task Reversal_preserves_history_and_cannot_be_applied_twice()
    {
        await using var test = await SqlTestDatabase.CreateAsync(); var f = await SetupAsync(test);
        var id = await Post(test, f, f.Draft()); var key = Guid.NewGuid();
        Task<long> Reverse(Guid source) => With<IJournalService, long>(test, s => s.ReverseAsync(f.Owner, id, Date.AddDays(1), source));
        var reversal = await Reverse(key); Assert.Equal(reversal, await Reverse(key));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Reverse(Guid.NewGuid()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => With<IJournalService, long>(test, s => s.ReverseAsync(f.Owner, reversal, Date.AddDays(2), Guid.NewGuid())));
        var stored = await With<IJournalService, PostedJournal>(test, s => s.GetAsync(f.Owner, reversal));
        Assert.Equal(id, stored.ReversesEntryId); Assert.Equal(JournalSide.Credit, stored.Lines[0].Side);
        Assert.All(stored.Lines, line => Assert.Equal(Date, line.ValuationDate));
        var balances = await With<IJournalService, IReadOnlyList<LedgerBalance>>(test, s => s.BalancesAsync(f.Owner, f.Branch, f.Currency, Date.AddDays(1)));
        Assert.All(balances, b => { Assert.Equal(b.Debit, b.Credit); Assert.Equal(b.BaseDebit, b.BaseCredit); });
        var before = await With<IJournalService, IReadOnlyList<LedgerBalance>>(test, s => s.BalancesAsync(f.Owner, f.Branch, f.Currency, Date));
        Assert.Equal(100m, before.Sum(b => b.Debit));
    }

    [SqlServerFact]
    public async Task Opening_is_counted_once_even_with_a_different_source()
    {
        await using var test = await SqlTestDatabase.CreateAsync(); var f = await SetupAsync(test);
        var draft = f.Draft(opening: true); var id = await Post(test, f, draft);
        Assert.Equal(id, await Post(test, f, draft));
        await Assert.ThrowsAsync<SqlException>(() => Post(test, f, f.Draft(opening: true)));
        Assert.Equal(1, await Count(test, "JournalEntries")); Assert.Equal(2, await Count(test, "JournalOpeningAccounts"));
    }

    [SqlServerFact]
    public async Task Closed_period_rejects_new_post_and_reversal_but_allows_safe_retry()
    {
        await using var test = await SqlTestDatabase.CreateAsync(); var f = await SetupAsync(test);
        var draft = f.Draft(); var id = await Post(test, f, draft);
        await Change<IFiscalPeriodService>(test, s => s.CloseAsync(f.Owner, f.Period));
        Assert.Equal(id, await Post(test, f, draft));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Post(test, f, f.Draft()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => With<IJournalService, long>(test, s => s.ReverseAsync(f.Owner, id, Date, Guid.NewGuid())));
        Assert.Equal(1, await Count(test, "JournalEntries"));
    }

    [SqlServerFact]
    public async Task Suspended_account_wrong_currency_and_wrong_branch_reject_posting()
    {
        await using var test = await SqlTestDatabase.CreateAsync(); var f = await SetupAsync(test);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Post(test, f, f.Draft(currency: f.OtherCurrency)));
        var otherBranch = await With<IBranchSetupService, int>(test, s => s.CreateAsync(f.Owner, "Other"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Post(test, f, f.Draft(branch: otherBranch)));
        await Change<IChartOfAccountsService>(test, s => s.SetAccountSuspendedAsync(f.Owner, f.Debit, true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Post(test, f, f.Draft()));
        Assert.Equal(0, await Count(test, "JournalEntries"));
    }

    [SqlServerFact]
    public async Task Permissions_require_explicit_branch_access_and_distinct_reverse_grant()
    {
        await using var test = await SqlTestDatabase.CreateAsync(); var f = await SetupAsync(test); int clerkId;
        await using (var db = test.Context())
        {
            var clerk = new User("clerk", new HisabTak.Infrastructure.Security.BCryptPasswordHasher().Hash("Test-" + Guid.NewGuid().ToString("N"))); db.Users.Add(clerk); await db.SaveChangesAsync(); clerkId = clerk.Id;
            var permission = await db.Permissions.SingleAsync(x => x.Code == PermissionCodes.JournalPost);
            db.UserPermissions.Add(new UserPermission(clerkId, permission.Id)); await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => With<IJournalService, long>(test, s => s.PostAsync(clerkId, f.Draft())));
        await using (var db = test.Context())
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE dbo.Users SET BranchId={0} WHERE UserId={1}", f.Branch, clerkId);
        }
        var id = await With<IJournalService, long>(test, s => s.PostAsync(clerkId, f.Draft()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => With<IJournalService, long>(test, s => s.ReverseAsync(clerkId, id, Date, Guid.NewGuid())));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => With<IJournalService, PostedJournal>(test, s => s.GetAsync(clerkId, id)));
    }

    [SqlServerFact]
    public async Task Audit_failure_rolls_back_header_lines_and_opening_reservation()
    {
        await using var test = await SqlTestDatabase.CreateAsync(); var f = await SetupAsync(test);
        await using (var db = test.Context()) await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER dbo.RejectJournalAudit ON dbo.AuditLogs AFTER INSERT AS
            BEGIN SET NOCOUNT ON;
                IF EXISTS(SELECT 1 FROM inserted WHERE OperationType='JOURNAL_POSTED') THROW 51200,'Test failure',1;
            END
            """);
        await Assert.ThrowsAsync<DbUpdateException>(() => Post(test, f, f.Draft(opening: true)));
        Assert.Equal(0, await Count(test, "JournalEntries")); Assert.Equal(0, await Count(test, "JournalLines"));
        Assert.Equal(0, await Count(test, "JournalOpeningAccounts"));
    }

    [SqlServerFact]
    public async Task Sql_protects_posted_header_lines_account_identity_and_base_currency()
    {
        await using var test = await SqlTestDatabase.CreateAsync(); var f = await SetupAsync(test); var id = await Post(test, f, f.Draft());
        await using var db = test.Context();
        await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync("UPDATE dbo.JournalEntries SET EntryDate='20260101' WHERE JournalEntryId={0}", id));
        await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync("UPDATE dbo.JournalLines SET Amount=2 WHERE JournalEntryId={0}", id));
        await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync("DELETE dbo.JournalLines WHERE JournalEntryId={0}", id));
        await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync("UPDATE dbo.Accounts SET CurrencyId={0} WHERE AccountId={1}", f.OtherCurrency, f.Debit));
        await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync("UPDATE dbo.CompanyInfo SET BaseCurrencyId={0}", f.OtherCurrency));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Change<IJournalService>(test, s => s.SetBaseCurrencyAsync(f.Owner, f.OtherCurrency)));
    }

    [SqlServerFact]
    public async Task Group_service_and_sql_reject_long_cycles_and_duplicate_codes()
    {
        await using var test = await SqlTestDatabase.CreateAsync(); var f = await SetupAsync(test);
        var child = await With<IChartOfAccountsService, int>(test, s => s.CreateGroupAsync(f.Owner, "0091", "Child", f.Group));
        var leaf = await With<IChartOfAccountsService, int>(test, s => s.CreateGroupAsync(f.Owner, "00911", "Leaf", child));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Change<IChartOfAccountsService>(test, s => s.MoveGroupAsync(f.Owner, f.Group, leaf)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => With<IChartOfAccountsService, int>(test, s => s.CreateGroupAsync(f.Owner, " 0091 ", "Duplicate", null)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => With<IChartOfAccountsService, int>(test, s => s.CreateGroupAsync(f.Owner, "0092", "Invalid", int.MaxValue)));
        await using var db = test.Context();
        await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync("UPDATE dbo.AccountMainGroups SET ParentMainGroupId={0} WHERE MainGroupId={1}", leaf, f.Group));
        Assert.Null((await db.AccountMainGroups.SingleAsync(x => x.Id == f.Group)).ParentMainGroupId);
    }

    [SqlServerFact]
    public async Task Precision_overflow_is_rejected_without_silent_rounding_or_partial_rows()
    {
        await using var test = await SqlTestDatabase.CreateAsync(); var f = await SetupAsync(test);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Post(test, f, f.Draft(amount: 0.000000001m)));
        Assert.Equal(0, await Count(test, "JournalEntries"));
    }
    [SqlServerFact]
    public async Task Foreign_currency_history_survives_rate_changes_and_reversal()
    {
        await using var test = await SqlTestDatabase.CreateAsync(); var f = await SetupAsync(test);
        var foreignAccount = await With<IChartOfAccountsService, int>(test,
            s => s.CreateAccountAsync(f.Owner, "009003", "Foreign", f.Group, f.OtherCurrency, 1, f.Branch));
        var period = new FiscalPeriod("proposal", Date, Date);
        var draft = JournalDraft.Create(f.Branch, period, Date, Guid.NewGuid(), [
            new(foreignAccount, JournalSide.Debit, new(new(10m, f.OtherCurrency), f.Currency, 3.8m, Date)),
            new(f.Credit, JournalSide.Credit, new(new(38m, f.Currency), f.Currency, 1m, Date))]);
        var id = await Post(test, f, draft);
        await using (var db = test.Context()) await db.Database.ExecuteSqlRawAsync("UPDATE dbo.Currencies SET ExchangeRate=99 WHERE CurrencyId={0}", f.OtherCurrency);
        var reverseId = await With<IJournalService, long>(test, s => s.ReverseAsync(f.Owner, id, Date.AddDays(1), Guid.NewGuid()));
        var reversed = await With<IJournalService, PostedJournal>(test, s => s.GetAsync(f.Owner, reverseId));
        Assert.Equal(3.8m, reversed.Lines[0].RateToBase); Assert.Equal(38m, reversed.Lines[0].BaseAmount);
        Assert.Equal(10m, reversed.Lines[0].Amount);
    }

    [SqlServerFact]
    public async Task Concurrent_different_reversals_commit_only_one()
    {
        await using var test = await SqlTestDatabase.CreateAsync(); var f = await SetupAsync(test); var id = await Post(test, f, f.Draft());
        async Task<bool> Reverse()
        {
            try { await With<IJournalService, long>(test, s => s.ReverseAsync(f.Owner, id, Date, Guid.NewGuid())); return true; }
            catch (InvalidOperationException e) when (e.Message == "Journal has already been reversed.") { return false; }
        }
        Assert.Single(await Task.WhenAll(Reverse(), Reverse()), success => success);
        Assert.Equal(2, await Count(test, "JournalEntries"));
    }

    [SqlServerFact]
    public async Task Concurrent_close_and_post_serialize_without_posting_after_close()
    {
        await using var test = await SqlTestDatabase.CreateAsync(); var f = await SetupAsync(test);
        async Task<bool> Attempt()
        {
            try { await Post(test, f, f.Draft()); return true; }
            catch (InvalidOperationException e) when (e.Message == "Posting to a closed fiscal period is not allowed.") { return false; }
        }
        var attempt = Attempt(); var close = Change<IFiscalPeriodService>(test, s => s.CloseAsync(f.Owner, f.Period));
        await Task.WhenAll(attempt, close);
        Assert.Equal(await attempt ? 1 : 0, await Count(test, "JournalEntries"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Post(test, f, f.Draft()));
    }

    [SqlServerFact]
    public async Task Legacy_opening_field_is_not_added_to_ledger_and_reservation_is_immutable()
    {
        await using var test = await SqlTestDatabase.CreateAsync(); var f = await SetupAsync(test);
        await using (var db = test.Context()) await db.Database.ExecuteSqlRawAsync("UPDATE dbo.Accounts SET OpeningBalance=100, OpeningBalanceNature=1 WHERE AccountId={0}", f.Debit);
        await Post(test, f, f.Draft(opening: true));
        var balances = await With<IJournalService, IReadOnlyList<LedgerBalance>>(test, s => s.BalancesAsync(f.Owner, f.Branch, f.Currency, Date));
        Assert.Equal(100m, balances.Single(x => x.AccountId == f.Debit).Debit);
        await using var verify = test.Context();
        await Assert.ThrowsAsync<SqlException>(() => verify.Database.ExecuteSqlRawAsync("DELETE dbo.JournalOpeningAccounts"));
        Assert.Equal(100m, (await verify.Accounts.SingleAsync(x => x.Id == f.Debit)).OpeningBalance);
    }

    [SqlServerFact]
    public async Task Sql_cannot_seal_an_unbalanced_journal()
    {
        await using var test = await SqlTestDatabase.CreateAsync(); var f = await SetupAsync(test);
        await using var db = test.Context();
        await db.Database.ExecuteSqlRawAsync("""
            INSERT dbo.JournalEntries(SourceKey,ContentHash,BranchId,FiscalPeriodId,EntryDate,Kind,CreatedBy)
                VALUES(NEWID(),REPLICATE('0',64),{0},{1},'20260601',1,{2});
            DECLARE @id bigint=SCOPE_IDENTITY();
            INSERT dbo.JournalLines(JournalEntryId,LineNumber,AccountId,Side,Amount,CurrencyId,RateToBase,BaseAmount,BaseCurrencyId,ValuationDate)
                VALUES(@id,1,{3},1,100,{5},1,100,{5},'20260601'),(@id,2,{4},2,99,{5},1,99,{5},'20260601');
            """, f.Branch, f.Period, f.Owner, f.Debit, f.Credit, f.Currency);
        var error = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync("UPDATE dbo.JournalEntries SET PostedAt=SYSUTCDATETIME()"));
        Assert.Equal(51102, error.Number);
        Assert.Empty(await With<IJournalService, IReadOnlyList<LedgerBalance>>(test, s => s.BalancesAsync(f.Owner, f.Branch, f.Currency, Date)));
    }

    [SqlServerFact]
    public async Task Group_audit_failure_rolls_back_group_creation()
    {
        await using var test = await SqlTestDatabase.CreateAsync(); var f = await SetupAsync(test);
        await using (var db = test.Context()) await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER dbo.RejectGroupAudit ON dbo.AuditLogs AFTER INSERT AS
            BEGIN SET NOCOUNT ON;
                IF EXISTS(SELECT 1 FROM inserted WHERE OperationType='ACCOUNT_GROUP_CREATED') THROW 51201,'Test failure',1;
            END
            """);
        await Assert.ThrowsAsync<DbUpdateException>(() => With<IChartOfAccountsService, int>(test, s => s.CreateGroupAsync(f.Owner, "0099", "Rollback", f.Group)));
        await using var verify = test.Context();
        Assert.False(await verify.AccountMainGroups.AnyAsync(x => x.Code == "0099"));
    }

}
