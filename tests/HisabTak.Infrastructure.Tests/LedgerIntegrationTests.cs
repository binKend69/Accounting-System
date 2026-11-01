using HisabTak.Application.Accounting;
using HisabTak.Application.Inventory;
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

    private static async Task<Fixture> SetupAsync(SqlTestDatabase test, bool configureRounding = true)
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
        var rounding = await chart.CreateAccountAsync(owner, "009099", "Rounding", group, currency, 1, branch);
        if (configureRounding) await scope.ServiceProvider.GetRequiredService<IJournalService>().ConfigureRoundingAsync(owner, branch, rounding, 2);
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
    public async Task Inventory_setup_scopes_locations_and_audits_trimmed_names()
    {
        await using var test = await SqlTestDatabase.CreateAsync(); var f = await SetupAsync(test);
        var other = await With<IBranchSetupService, int>(test, x => x.CreateAsync(f.Owner, "Other inventory branch"));
        var warehouse = await With<IInventorySetupService, int>(test, x => x.CreateAsync(f.Owner, f.Branch, InventoryReferenceKind.Warehouse, "  Main store  "));
        await With<IInventorySetupService, int>(test, x => x.CreateAsync(f.Owner, other, InventoryReferenceKind.Warehouse, "Main store"));
        var location = await With<IInventorySetupService, int>(test, x => x.CreateAsync(f.Owner, f.Branch, InventoryReferenceKind.Location, "Shelf A", warehouse));
        await Assert.ThrowsAsync<InvalidOperationException>(() => With<IInventorySetupService, int>(test, x => x.CreateAsync(f.Owner, other, InventoryReferenceKind.Location, "Wrong branch", warehouse)));
        var data = await With<IInventorySetupService, InventorySetup>(test, x => x.LoadAsync(f.Owner, f.Branch));
        Assert.Equal("Main store", Assert.Single(data.Warehouses).Name);
        Assert.Equal(location, Assert.Single(data.Locations).Id);
        Assert.Equal(warehouse, data.Locations[0].ParentId);
        await using var db = test.Context();
        Assert.Equal(3, await db.AuditLogs.CountAsync(x => x.OperationType == "INVENTORY_REFERENCE_CREATED"));
        var entity = await db.Warehouses.SingleAsync(x => x.Id == warehouse); entity.SetActive(false); await db.SaveChangesAsync();
        data = await With<IInventorySetupService, InventorySetup>(test, x => x.LoadAsync(f.Owner, f.Branch));
        Assert.Empty(data.Warehouses); Assert.Empty(data.Locations);
        await Assert.ThrowsAsync<InvalidOperationException>(() => With<IInventorySetupService, int>(test, x => x.CreateAsync(f.Owner, f.Branch, InventoryReferenceKind.Location, "Inactive store", warehouse)));
    }

    [SqlServerFact]
    public async Task Inventory_setup_serializes_duplicate_names_and_reserves_deleted_names()
    {
        await using var test = await SqlTestDatabase.CreateAsync(); var f = await SetupAsync(test);
        async Task<bool> Create()
        {
            try { await With<IInventorySetupService, int>(test, x => x.CreateAsync(f.Owner, f.Branch, InventoryReferenceKind.Unit, "  Box of twelve  ")); return true; }
            catch (InvalidOperationException) { return false; }
        }
        var results = await Task.WhenAll(Create(), Create()); Assert.Single(results, x => x);
        await using var db = test.Context();
        var unit = await db.Units.SingleAsync(x => x.NameAr == "Box of twelve"); unit.MarkDeleted(); await db.SaveChangesAsync();
        Assert.False(await Create());
        Assert.Equal(1, await db.AuditLogs.CountAsync(x => x.OperationType == "INVENTORY_REFERENCE_CREATED"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => With<IInventorySetupService, int>(test, x => x.CreateAsync(f.Owner, f.Branch, InventoryReferenceKind.Unit, new string('a', 51))));
        await Assert.ThrowsAsync<ArgumentException>(() => With<IInventorySetupService, int>(test, x => x.CreateAsync(f.Owner, f.Branch, InventoryReferenceKind.Unit, "Invalid parent", 1)));
    }

    [SqlServerFact]
    public async Task Inventory_setup_requires_permission_and_explicit_cross_branch_access()
    {
        await using var test = await SqlTestDatabase.CreateAsync(); var f = await SetupAsync(test); int actor;
        await using (var db = test.Context())
        {
            var user = new HisabTak.Domain.Security.User("inventory-clerk", new HisabTak.Infrastructure.Security.BCryptPasswordHasher().Hash("Test-" + Guid.NewGuid().ToString("N")));
            db.Users.Add(user); await db.SaveChangesAsync(); actor = user.Id;
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => With<IInventorySetupService, InventorySetup>(test, x => x.LoadAsync(actor, f.Branch)));
        await using (var db = test.Context())
        {
            var permission = await db.Permissions.SingleAsync(x => x.Code == PermissionCodes.SetupManage);
            db.UserPermissions.Add(new UserPermission(actor, permission.Id)); await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => With<IInventorySetupService, int>(test, x => x.CreateAsync(actor, f.Branch, InventoryReferenceKind.Warehouse, "Denied")));
        await using var verify = test.Context(); Assert.False(await verify.Warehouses.AnyAsync());
    }

    [SqlServerFact]
    public async Task Inventory_setup_rolls_back_reference_when_audit_fails()
    {
        await using var test = await SqlTestDatabase.CreateAsync(); var f = await SetupAsync(test);
        await using (var db = test.Context())
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER dbo.TR_TestInventoryAudit ON dbo.AuditLogs AFTER INSERT AS BEGIN IF EXISTS(SELECT 1 FROM inserted WHERE OperationType='INVENTORY_REFERENCE_CREATED') THROW 51205,'Test failure',1; END");
        await Assert.ThrowsAnyAsync<Exception>(() => With<IInventorySetupService, int>(test, x => x.CreateAsync(f.Owner, f.Branch, InventoryReferenceKind.Warehouse, "Rollback store")));
        await using var verify = test.Context(); Assert.False(await verify.Warehouses.AnyAsync());
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

    [SqlServerFact]
    public async Task Rounded_journal_preserves_exact_values_and_reverses_historical_rounding_after_configuration_change()
    {
        await using var test = await SqlTestDatabase.CreateAsync(); var f = await SetupAsync(test);
        var foreign = await With<IChartOfAccountsService, int>(test, s => s.CreateAccountAsync(f.Owner, "009003", "Foreign", f.Group, f.OtherCurrency, 1, f.Branch));
        var draft = JournalDraft.Create(f.Branch, new("proposal", Date, Date), Date, Guid.NewGuid(), [
            new(foreign, JournalSide.Debit, new(new(0.01m, f.OtherCurrency), f.Currency, 1.5m, Date)),
            new(foreign, JournalSide.Debit, new(new(0.01m, f.OtherCurrency), f.Currency, 1.5m, Date)),
            new(f.Credit, JournalSide.Credit, new(new(0.03m, f.Currency), f.Currency, 1m, Date))]);
        var id = await Post(test, f, draft);
        var stored = await With<IJournalService, PostedJournal>(test, s => s.GetAsync(f.Owner, id));
        Assert.Equal(2, stored.RoundingPrecision); Assert.Equal(4, stored.Lines.Count);
        Assert.Equal(0.015m, stored.Lines[0].BaseAmount); Assert.Equal(0.02m, stored.Lines[0].BookBaseAmount);
        Assert.Equal(0.01m, Assert.Single(stored.Lines, l => l.IsRounding).BookBaseAmount);
        var replacement = await With<IChartOfAccountsService, int>(test, s => s.CreateAccountAsync(f.Owner, "009098", "New rounding", f.Group, f.Currency, 1, f.Branch));
        await Change<IJournalService>(test, s => s.ConfigureRoundingAsync(f.Owner, f.Branch, replacement, 2));
        Assert.Equal(id, await Post(test, f, draft));
        var reversedId = await With<IJournalService, long>(test, s => s.ReverseAsync(f.Owner, id, Date.AddDays(1), Guid.NewGuid()));
        var reversed = await With<IJournalService, PostedJournal>(test, s => s.GetAsync(f.Owner, reversedId));
        Assert.Equal(stored.Lines.Select(x => x.BookBaseAmount), reversed.Lines.Select(x => x.BookBaseAmount));
        Assert.Equal(stored.Lines.Select(x => x.AccountId), reversed.Lines.Select(x => x.AccountId));
        Assert.All(await With<IJournalService, IReadOnlyList<LedgerBalance>>(test, s => s.BalancesAsync(f.Owner, f.Branch, f.Currency, Date.AddDays(1))),
            b => Assert.Equal(b.BaseDebit, b.BaseCredit));
    }

    [SqlServerFact]
    public async Task Missing_rounding_setup_and_inactive_difference_account_cannot_post()
    {
        await using var test = await SqlTestDatabase.CreateAsync(); var f = await SetupAsync(test);
        await using (var db = test.Context()) await db.Database.ExecuteSqlRawAsync("DELETE dbo.LedgerRoundingSettings");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Post(test, f, f.Draft()));
        Assert.Equal(0, await Count(test, "JournalEntries"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Change<IJournalService>(test, s => s.ConfigureRoundingAsync(f.Owner, f.Branch, f.Debit, 3)));
    }

    [SqlServerFact]
    public async Task Document_draft_posts_once_and_cancellation_preserves_its_number()
    {
        await using var test = await SqlTestDatabase.CreateAsync(); var f = await SetupAsync(test); var draft = f.Draft();
        var saved = await With<IJournalDocumentService, JournalDocument>(test, s => s.SaveDraftAsync(f.Owner, draft));
        Assert.Equal(JournalDocumentState.Draft, saved.State); Assert.Null(saved.Number);
        Assert.Equal(0, await Count(test, "JournalEntries"));
        Assert.Equal(saved, await With<IJournalDocumentService, JournalDocument>(test, s => s.SaveDraftAsync(f.Owner, draft)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Post(test, f, draft));
        await Assert.ThrowsAsync<InvalidOperationException>(() => With<IJournalDocumentService, JournalDocument>(test, s => s.ReverseAsync(f.Owner, saved.Id, Date, Guid.NewGuid())));
        Task<JournalDocument> PostDocument() => With<IJournalDocumentService, JournalDocument>(test, s => s.PostAsync(f.Owner, saved.Id));
        var results = await Task.WhenAll(PostDocument(), PostDocument());
        Assert.Equal(results[0], results[1]); Assert.Equal(1, results[0].Number);
        Assert.Equal(JournalDocumentState.Posted, results[0].State); Assert.Equal(1, await Count(test, "JournalEntries"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => With<IJournalService, long>(test, s => s.ReverseAsync(f.Owner, results[0].JournalId!.Value, Date, Guid.NewGuid())));
        var reversed = await With<IJournalDocumentService, JournalDocument>(test, s => s.ReverseAsync(f.Owner, saved.Id, Date, Guid.NewGuid()));
        Assert.Equal(JournalDocumentState.Reversed, reversed.State); Assert.Equal(1, reversed.Number);
        Assert.Equal(reversed, await PostDocument());
        Assert.Equal(reversed, await With<IJournalDocumentService, JournalDocument>(test, s => s.ReverseAsync(f.Owner, saved.Id, Date, Guid.NewGuid())));
        Assert.Equal(2, await Count(test, "JournalEntries"));
        var next = await With<IJournalDocumentService, JournalDocument>(test, s => s.SaveDraftAsync(f.Owner, f.Draft()));
        Assert.Equal(2, (await With<IJournalDocumentService, JournalDocument>(test, s => s.PostAsync(f.Owner, next.Id))).Number);
    }

    [SqlServerFact]
    public async Task Document_audit_failure_rolls_back_number_and_entire_journal()
    {
        await using var test = await SqlTestDatabase.CreateAsync(); var f = await SetupAsync(test);
        var saved = await With<IJournalDocumentService, JournalDocument>(test, s => s.SaveDraftAsync(f.Owner, f.Draft()));
        await using (var db = test.Context()) await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER dbo.RejectDocumentAudit ON dbo.AuditLogs AFTER INSERT AS
            BEGIN SET NOCOUNT ON;
                IF EXISTS(SELECT 1 FROM inserted WHERE OperationType='DOCUMENT_POSTED') THROW 51202,'Test failure',1;
            END
            """);
        await Assert.ThrowsAsync<DbUpdateException>(() => With<IJournalDocumentService, JournalDocument>(test, s => s.PostAsync(f.Owner, saved.Id)));
        Assert.Equal(0, await Count(test, "JournalEntries")); Assert.Equal(0, await Count(test, "JournalLines"));
        Assert.Equal(saved, await With<IJournalDocumentService, JournalDocument>(test, s => s.GetAsync(f.Owner, saved.Id)));
        await using (var db = test.Context())
        {
            Assert.Equal(0, await db.Database.SqlQueryRaw<long>("SELECT COUNT_BIG(*) AS Value FROM dbo.DocumentNumbers").SingleAsync());
            Assert.False(await db.AuditLogs.AnyAsync(x => x.OperationType == "JOURNAL_POSTED"));
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER dbo.RejectDocumentAudit");
        }
        var posted = await With<IJournalDocumentService, JournalDocument>(test, s => s.PostAsync(f.Owner, saved.Id));
        Assert.Equal(1, posted.Number);
    }

    [SqlServerFact]
    public async Task Concurrent_documents_get_unique_numbers_and_opening_has_its_own_sequence()
    {
        await using var test = await SqlTestDatabase.CreateAsync(); var f = await SetupAsync(test);
        var first = await With<IJournalDocumentService, JournalDocument>(test, s => s.SaveDraftAsync(f.Owner, f.Draft()));
        var second = await With<IJournalDocumentService, JournalDocument>(test, s => s.SaveDraftAsync(f.Owner, f.Draft()));
        var posted = await Task.WhenAll(
            With<IJournalDocumentService, JournalDocument>(test, s => s.PostAsync(f.Owner, first.Id)),
            With<IJournalDocumentService, JournalDocument>(test, s => s.PostAsync(f.Owner, second.Id)));
        Assert.Equal(new long?[] { 1, 2 }, posted.Select(x => x.Number).Order());
        var opening = await With<IJournalDocumentService, JournalDocument>(test, s => s.SaveDraftAsync(f.Owner, f.Draft(opening: true)));
        var result = await With<IJournalDocumentService, JournalDocument>(test, s => s.PostAsync(f.Owner, opening.Id));
        Assert.Equal("JOURNAL_OPENING", result.TypeCode); Assert.Equal(1, result.Number);
    }

    [SqlServerFact]
    public async Task Posted_document_cannot_be_rewritten_and_source_content_cannot_change()
    {
        await using var test = await SqlTestDatabase.CreateAsync(); var f = await SetupAsync(test); var draft = f.Draft();
        var saved = await With<IJournalDocumentService, JournalDocument>(test, s => s.SaveDraftAsync(f.Owner, draft));
        await Assert.ThrowsAsync<InvalidOperationException>(() => With<IJournalDocumentService, JournalDocument>(test, s => s.SaveDraftAsync(f.Owner, f.Draft(draft.SourceKey, 200))));
        await With<IJournalDocumentService, JournalDocument>(test, s => s.PostAsync(f.Owner, saved.Id));
        await using var db = test.Context();
        await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync("UPDATE dbo.JournalDocuments SET DocumentNumber=99 WHERE DocumentId={0}", saved.Id));
        await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync("DELETE dbo.JournalDocuments WHERE DocumentId={0}", saved.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => With<IJournalDocumentService, JournalDocument>(test, s => s.GetAsync(0, saved.Id)));
    }

    [SqlServerFact]
    public async Task Upgrade_keeps_legacy_exact_balances_and_reversal_without_reinterpretation()
    {
        await using var test = await SqlTestDatabase.CreateAsync("20261031000300_HardenLedgerHistory"); var f = await SetupAsync(test, false);
        await using (var db = test.Context())
        {
            await db.Database.ExecuteSqlRawAsync("""
                INSERT dbo.JournalEntries(SourceKey,ContentHash,BranchId,FiscalPeriodId,EntryDate,Kind,CreatedBy)
                VALUES(NEWID(),REPLICATE('0',64),{0},{1},'20260601',1,{2});
                DECLARE @id bigint=SCOPE_IDENTITY();
                INSERT dbo.JournalLines(JournalEntryId,LineNumber,AccountId,Side,Amount,CurrencyId,RateToBase,BaseAmount,BaseCurrencyId,ValuationDate)
                VALUES(@id,1,{3},1,0.015,{5},1,0.015,{5},'20260601'),(@id,2,{4},2,0.015,{5},1,0.015,{5},'20260601');
                UPDATE dbo.JournalEntries SET PostedAt=SYSUTCDATETIME() WHERE JournalEntryId=@id;
                """, f.Branch, f.Period, f.Owner, f.Debit, f.Credit, f.Currency);
            await db.Database.MigrateAsync();
        }
        var original = await With<IJournalService, PostedJournal>(test, s => s.GetAsync(f.Owner, 1));
        Assert.Null(original.RoundingPrecision); Assert.All(original.Lines, l => Assert.Null(l.BookBaseAmount));
        var balances = await With<IJournalService, IReadOnlyList<LedgerBalance>>(test, s => s.BalancesAsync(f.Owner, f.Branch, f.Currency, Date));
        Assert.Equal(0.015m, balances.Sum(b => b.BaseDebit));
        var reversedId = await With<IJournalService, long>(test, s => s.ReverseAsync(f.Owner, 1, Date.AddDays(1), Guid.NewGuid()));
        var reversed = await With<IJournalService, PostedJournal>(test, s => s.GetAsync(f.Owner, reversedId));
        Assert.Null(reversed.RoundingPrecision); Assert.All(reversed.Lines, l => Assert.Equal(0.015m, l.BaseAmount));
    }

    [SqlServerFact]
    public async Task Cancellation_audit_failure_rolls_back_reversal_and_document_state()
    {
        await using var test = await SqlTestDatabase.CreateAsync(); var f = await SetupAsync(test);
        var draft = await With<IJournalDocumentService, JournalDocument>(test, s => s.SaveDraftAsync(f.Owner, f.Draft()));
        var posted = await With<IJournalDocumentService, JournalDocument>(test, s => s.PostAsync(f.Owner, draft.Id));
        await using (var db = test.Context()) await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER dbo.RejectDocumentReversalAudit ON dbo.AuditLogs AFTER INSERT AS
            BEGIN SET NOCOUNT ON;
                IF EXISTS(SELECT 1 FROM inserted WHERE OperationType='DOCUMENT_REVERSED') THROW 51203,'Test failure',1;
            END
            """);
        await Assert.ThrowsAsync<DbUpdateException>(() => With<IJournalDocumentService, JournalDocument>(test, s => s.ReverseAsync(f.Owner, posted.Id, Date, Guid.NewGuid())));
        Assert.Equal(posted, await With<IJournalDocumentService, JournalDocument>(test, s => s.GetAsync(f.Owner, posted.Id)));
        Assert.Equal(1, await Count(test, "JournalEntries"));
        await using var verify = test.Context();
        Assert.False(await verify.AuditLogs.AnyAsync(x => x.OperationType == "JOURNAL_REVERSED"));
    }

    [SqlServerFact]
    public async Task Numbering_is_independent_for_each_branch_and_calendar_year()
    {
        await using var test = await SqlTestDatabase.CreateAsync(); var f = await SetupAsync(test);
        var first = await With<IJournalDocumentService, JournalDocument>(test, s => s.SaveDraftAsync(f.Owner, f.Draft()));
        Assert.Equal(1, (await With<IJournalDocumentService, JournalDocument>(test, s => s.PostAsync(f.Owner, first.Id))).Number);
        var branch = await With<IBranchSetupService, int>(test, s => s.CreateAsync(f.Owner, "Second"));
        var debit = await With<IChartOfAccountsService, int>(test, s => s.CreateAccountAsync(f.Owner, "0201", "Debit", f.Group, f.Currency, 1, branch));
        var credit = await With<IChartOfAccountsService, int>(test, s => s.CreateAccountAsync(f.Owner, "0202", "Credit", f.Group, f.Currency, 2, branch));
        var rounding = await With<IChartOfAccountsService, int>(test, s => s.CreateAccountAsync(f.Owner, "0299", "Rounding", f.Group, f.Currency, 1, branch));
        await Change<IJournalService>(test, s => s.ConfigureRoundingAsync(f.Owner, branch, rounding, 2));
        JournalDraft Build(int branchId, int debitId, int creditId, DateOnly date) => JournalDraft.Create(branchId, new("proposal", date, date), date, Guid.NewGuid(), [
            new(debitId, JournalSide.Debit, new(new(1m, f.Currency), f.Currency, 1m, date)),
            new(creditId, JournalSide.Credit, new(new(1m, f.Currency), f.Currency, 1m, date))]);
        var second = await With<IJournalDocumentService, JournalDocument>(test, s => s.SaveDraftAsync(f.Owner, Build(branch, debit, credit, Date)));
        Assert.Equal(1, (await With<IJournalDocumentService, JournalDocument>(test, s => s.PostAsync(f.Owner, second.Id))).Number);
        await using (var db = test.Context())
        {
            db.FiscalPeriods.Add(new("2027", new(2027,1,1), new(2027,12,31))); await db.SaveChangesAsync();
        }
        var nextYear = await With<IJournalDocumentService, JournalDocument>(test, s => s.SaveDraftAsync(f.Owner, Build(f.Branch, f.Debit, f.Credit, new(2027,1,1))));
        Assert.Equal(1, (await With<IJournalDocumentService, JournalDocument>(test, s => s.PostAsync(f.Owner, nextYear.Id))).Number);
    }

    [SqlServerFact]
    public async Task Draft_edits_detect_conflicts_and_post_only_the_reviewed_version()
    {
        await using var test = await SqlTestDatabase.CreateAsync(); var f = await SetupAsync(test); var original = f.Draft();
        var saved = await With<IJournalDocumentService, JournalDocument>(test, s => s.SaveDraftAsync(f.Owner, original));
        var changed = f.Draft(original.SourceKey, 200);
        var edited = await With<IJournalDocumentService, JournalDocument>(test, s => s.UpdateDraftAsync(f.Owner, saved.Id, saved.Version, changed));
        Assert.Equal(2, edited.Version);
        Assert.Equal(edited, await With<IJournalDocumentService, JournalDocument>(test, s => s.UpdateDraftAsync(f.Owner, saved.Id, saved.Version, changed)));
        await Assert.ThrowsAsync<DocumentConflictException>(() => With<IJournalDocumentService, JournalDocument>(test, s => s.UpdateDraftAsync(f.Owner, saved.Id, saved.Version, f.Draft(original.SourceKey, 300))));
        await Assert.ThrowsAsync<DocumentConflictException>(() => With<IJournalDocumentService, JournalDocument>(test, s => s.PostVersionAsync(f.Owner, saved.Id, saved.Version)));
        Assert.Equal(0, await Count(test, "JournalEntries"));
        var posted = await With<IJournalDocumentService, JournalDocument>(test, s => s.PostVersionAsync(f.Owner, saved.Id, edited.Version));
        Assert.Equal(posted, await With<IJournalDocumentService, JournalDocument>(test, s => s.PostVersionAsync(f.Owner, saved.Id, edited.Version)));
        await Assert.ThrowsAsync<DocumentConflictException>(() => With<IJournalDocumentService, JournalDocument>(test, s => s.PostVersionAsync(f.Owner, saved.Id, saved.Version)));
        var journal = await With<IJournalService, PostedJournal>(test, s => s.GetAsync(f.Owner, posted.JournalId!.Value));
        Assert.All(journal.Lines, l => Assert.Equal(200m, l.Amount));
        await Assert.ThrowsAsync<InvalidOperationException>(() => With<IJournalDocumentService, JournalDocument>(test, s => s.UpdateDraftAsync(f.Owner, saved.Id, posted.Version, original)));
    }

    [SqlServerFact]
    public async Task Concurrent_different_draft_edits_cannot_overwrite_each_other()
    {
        await using var test = await SqlTestDatabase.CreateAsync(); var f = await SetupAsync(test); var original = f.Draft();
        var saved = await With<IJournalDocumentService, JournalDocument>(test, s => s.SaveDraftAsync(f.Owner, original));
        async Task<bool> Edit(decimal amount)
        {
            try { await With<IJournalDocumentService, JournalDocument>(test, s => s.UpdateDraftAsync(f.Owner, saved.Id, 1, f.Draft(original.SourceKey, amount))); return true; }
            catch (DocumentConflictException) { return false; }
        }
        Assert.Single(await Task.WhenAll(Edit(200), Edit(300)), success => success);
        var details = await With<IJournalDocumentService, JournalDocumentDetails>(test, s => s.DetailsAsync(f.Owner, saved.Id));
        Assert.Equal(2, details.Document.Version);
        Assert.Contains(details.Draft.Lines[0].Valuation.Original.Amount, new[] { 200m, 300m });
    }

    [SqlServerFact]
    public async Task Draft_edit_audit_failure_restores_payload_and_version()
    {
        await using var test = await SqlTestDatabase.CreateAsync(); var f = await SetupAsync(test); var original = f.Draft();
        var saved = await With<IJournalDocumentService, JournalDocument>(test, s => s.SaveDraftAsync(f.Owner, original));
        await using (var db = test.Context()) await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER dbo.RejectEditAudit ON dbo.AuditLogs AFTER INSERT AS
            BEGIN SET NOCOUNT ON;
                IF EXISTS(SELECT 1 FROM inserted WHERE OperationType='DOCUMENT_DRAFT_UPDATED') THROW 51204,'Test failure',1;
            END
            """);
        await Assert.ThrowsAsync<DbUpdateException>(() => With<IJournalDocumentService, JournalDocument>(test, s => s.UpdateDraftAsync(f.Owner, saved.Id, 1, f.Draft(original.SourceKey, 200))));
        var details = await With<IJournalDocumentService, JournalDocumentDetails>(test, s => s.DetailsAsync(f.Owner, saved.Id));
        Assert.Equal(1, details.Document.Version); Assert.Equal(100m, details.Draft.Lines[0].Valuation.Original.Amount);
    }

    [SqlServerFact]
    public async Task Cash_receipt_and_payment_use_classified_accounts_and_independent_sequences()
    {
        await using var test = await SqlTestDatabase.CreateAsync(); var f = await SetupAsync(test); int cashGroup;
        await using (var db = test.Context()) cashGroup = (await db.AccountGeneralGroups.SingleAsync(x => x.SystemCode == "CASH")).Id;
        await Change<IChartOfAccountsService>(test, s => s.SetGeneralGroupAsync(f.Owner, f.Debit, cashGroup));
        var receipt = await With<IJournalDocumentService, JournalDocument>(test, s => s.SaveVoucherAsync(f.Owner, f.Draft(), "RECEIPT_VOUCHER"));
        var postedReceipt = await With<IJournalDocumentService, JournalDocument>(test, s => s.PostVersionAsync(f.Owner, receipt.Id, receipt.Version));
        var paymentDraft = JournalDraft.Create(f.Branch, new("proposal", Date, Date), Date, Guid.NewGuid(), [
            new(f.Debit, JournalSide.Credit, new(new(100m, f.Currency), f.Currency, 1m, Date)),
            new(f.Credit, JournalSide.Debit, new(new(100m, f.Currency), f.Currency, 1m, Date))]);
        var payment = await With<IJournalDocumentService, JournalDocument>(test, s => s.SaveVoucherAsync(f.Owner, paymentDraft, "PAYMENT_VOUCHER"));
        var postedPayment = await With<IJournalDocumentService, JournalDocument>(test, s => s.PostVersionAsync(f.Owner, payment.Id, payment.Version));
        Assert.Equal(1, postedReceipt.Number); Assert.Equal(1, postedPayment.Number);
        var balances = await With<IJournalService, IReadOnlyList<LedgerBalance>>(test, s => s.BalancesAsync(f.Owner, f.Branch, f.Currency, Date));
        Assert.All(balances, b => Assert.Equal(b.Debit, b.Credit));
        await Assert.ThrowsAsync<InvalidOperationException>(() => With<IJournalDocumentService, JournalDocument>(test, s => s.SaveVoucherAsync(f.Owner, f.Draft(), "PAYMENT_VOUCHER")));
    }

    [SqlServerFact]
    public async Task Voucher_revalidates_cash_classification_at_posting_and_listing_never_posts()
    {
        await using var test = await SqlTestDatabase.CreateAsync(); var f = await SetupAsync(test); int cashGroup, customerGroup;
        await using (var db = test.Context())
        {
            cashGroup = (await db.AccountGeneralGroups.SingleAsync(x => x.SystemCode == "CASH")).Id;
            customerGroup = (await db.AccountGeneralGroups.SingleAsync(x => x.SystemCode == "CUSTOMER")).Id;
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => With<IJournalDocumentService, JournalDocument>(test, s => s.SaveVoucherAsync(f.Owner, f.Draft(), "RECEIPT_VOUCHER")));
        await Change<IChartOfAccountsService>(test, s => s.SetGeneralGroupAsync(f.Owner, f.Debit, cashGroup));
        var receipt = await With<IJournalDocumentService, JournalDocument>(test, s => s.SaveVoucherAsync(f.Owner, f.Draft(), "RECEIPT_VOUCHER"));
        await Change<IChartOfAccountsService>(test, s => s.SetGeneralGroupAsync(f.Owner, f.Debit, customerGroup));
        await Assert.ThrowsAsync<InvalidOperationException>(() => With<IJournalDocumentService, JournalDocument>(test, s => s.PostVersionAsync(f.Owner, receipt.Id, 1)));
        Assert.Single(await With<IJournalDocumentService, IReadOnlyList<JournalDocument>>(test, s => s.ListAsync(f.Owner, f.Branch)));
        await With<IJournalDocumentService, JournalDocumentDetails>(test, s => s.DetailsAsync(f.Owner, receipt.Id));
        await With<IJournalDocumentService, JournalDocumentDetails>(test, s => s.DetailsAsync(f.Owner, receipt.Id));
        Assert.Equal(0, await Count(test, "JournalEntries"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => With<IJournalDocumentService, IReadOnlyList<JournalDocument>>(test, s => s.ListAsync(0, f.Branch)));
    }

    [SqlServerFact]
    public async Task Workspace_restricts_branch_and_accounts_using_server_permissions()
    {
        await using var test = await SqlTestDatabase.CreateAsync(); var f = await SetupAsync(test); int userId;
        var otherBranch = await With<IBranchSetupService, int>(test, s => s.CreateAsync(f.Owner, "Other"));
        await using (var db = test.Context())
        {
            var user = new User("reader", new HisabTak.Infrastructure.Security.BCryptPasswordHasher().Hash("Test-" + Guid.NewGuid().ToString("N")));
            db.Users.Add(user); await db.SaveChangesAsync(); userId = user.Id;
            await db.Database.ExecuteSqlRawAsync("UPDATE dbo.Users SET BranchId={0} WHERE UserId={1}", f.Branch, userId);
            var permission = await db.Permissions.SingleAsync(x => x.Code == PermissionCodes.JournalRead);
            db.UserPermissions.Add(new UserPermission(userId, permission.Id)); await db.SaveChangesAsync();
        }
        var workspace = await With<IAccountingWorkspaceService, AccountingWorkspace>(test, s => s.LoadAsync(userId, f.Branch));
        Assert.Equal(f.Branch, Assert.Single(workspace.Branches).Id);
        Assert.Contains(workspace.Accounts, x => x.Id == f.Debit);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => With<IAccountingWorkspaceService, AccountingWorkspace>(test, s => s.LoadAsync(userId, otherBranch)));
    }

    [SqlServerFact]
    public async Task Period_setup_from_workspace_is_authorized_audited_and_rejects_overlap()
    {
        await using var test = await SqlTestDatabase.CreateAsync(); var f = await SetupAsync(test);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => With<IFiscalPeriodService, int>(test, s => s.CreateAsync(0, "2027", new(2027,1,1), new(2027,12,31))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => With<IFiscalPeriodService, int>(test, s => s.CreateAsync(f.Owner, "Overlap", Date, Date.AddDays(1))));
        var id = await With<IFiscalPeriodService, int>(test, s => s.CreateAsync(f.Owner, "2027", new(2027,1,1), new(2027,12,31)));
        var workspace = await With<IAccountingWorkspaceService, AccountingWorkspace>(test, s => s.LoadAsync(f.Owner, f.Branch));
        Assert.Contains(workspace.Periods, p => p.Id == id);
        await using var db = test.Context();
        Assert.Single(await db.AuditLogs.Where(x => x.OperationType == "FISCAL_PERIOD_CREATED" && x.RecordId == id).ToListAsync());
    }

}
