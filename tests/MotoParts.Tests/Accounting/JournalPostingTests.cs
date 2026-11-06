using MotoParts.Core.Common;
using MotoParts.Core.Domain;
using MotoParts.Core.Services;
using MotoParts.Tests.Support;

namespace MotoParts.Tests.Accounting;

public sealed class JournalPostingTests : TestBase
{
    private static IReadOnlyList<LedgerLine> CashSale(decimal amount) =>
    [
        LedgerLine.Debit(AccountCodes.Cash, Money.Of(amount)),
        LedgerLine.Credit(AccountCodes.SalesRevenue, Money.Of(amount)),
    ];

    [Fact]
    public void A_balanced_entry_is_posted_with_totals_derived_from_its_lines()
    {
        var entry = App.Accounting.Post("بيع نقدي", CashSale(150.75m), JournalSource.Manual, Today, Owner());

        Assert.Equal("JE-2026-000001", entry.EntryNumber);
        Assert.True(entry.IsPosted);
        Assert.Equal(M(150.75m), entry.TotalDebit);
        Assert.Equal(M(150.75m), entry.TotalCredit);
        Assert.Equal(2, App.Accounting.GetLines(entry.Id).Count);
    }

    [Fact]
    public void An_unbalanced_entry_is_refused_and_leaves_nothing_behind()
    {
        var ex = Assert.Throws<BusinessRuleException>(() => App.Accounting.Post(
            "خطأ",
            [LedgerLine.Debit(AccountCodes.Cash, M(100)), LedgerLine.Credit(AccountCodes.SalesRevenue, M(99.99m))],
            JournalSource.Manual, Today, Owner()));

        Assert.Contains("غير متوازن", ex.Message);
        Assert.Equal(0L, RawScalar<long>("SELECT COUNT(*) FROM journal_entries"));

        // The number was not burned: the next good entry still gets 000001.
        var entry = App.Accounting.Post("بيع", CashSale(10), JournalSource.Manual, Today, null);
        Assert.Equal("JE-2026-000001", entry.EntryNumber);
    }

    [Fact]
    public void A_single_line_entry_is_refused()
    {
        Assert.Throws<BusinessRuleException>(() => App.Accounting.Post(
            "سطر واحد", [LedgerLine.Debit(AccountCodes.Cash, M(10))], JournalSource.Manual, Today, null));
    }

    [Fact]
    public void Zero_and_negative_lines_cannot_even_be_constructed()
    {
        Assert.Throws<BusinessRuleException>(() => LedgerLine.Debit(AccountCodes.Cash, Money.Zero));
        Assert.Throws<BusinessRuleException>(() => LedgerLine.Credit(AccountCodes.Cash, M(-5)));
    }

    [Fact]
    public void Posting_into_a_date_with_no_fiscal_period_is_refused()
    {
        var ex = Assert.Throws<ClosedFiscalPeriodException>(() =>
            App.Accounting.Post("قديم", CashSale(10), JournalSource.Manual, new DateOnly(2019, 1, 1), null));

        Assert.Contains("لا توجد فترة محاسبية", ex.Message);
    }

    [Fact]
    public void Posting_into_a_locked_period_is_refused()
    {
        var owner = Owner();
        var period = App.FiscalPeriods.Containing(Today)!;
        App.FiscalPeriods.Lock(period.Id, owner, "إقفال يونيو");

        Assert.Throws<ClosedFiscalPeriodException>(() =>
            App.Accounting.Post("متأخر", CashSale(10), JournalSource.Manual, Today, owner));
    }

    [Fact]
    public void Reversal_mirrors_the_original_and_the_pair_nets_to_zero()
    {
        var owner = Owner();
        var original = App.Accounting.Post("بيع", CashSale(80), JournalSource.Manual, Today, owner);

        var reversal = App.Accounting.Reverse(original.Id, Today, owner);

        Assert.True(reversal.IsReversal);
        Assert.Equal(original.Id, reversal.ReversesJournalEntryId);
        Assert.Equal(Money.Zero, App.Accounting.BalanceByCode(AccountCodes.Cash));
        Assert.Equal(Money.Zero, App.Accounting.BalanceByCode(AccountCodes.SalesRevenue));

        var lines = App.Accounting.GetLines(reversal.Id);
        Assert.Equal(M(80), lines.Single(l => l.IsDebit).Debit);
        Assert.Equal(App.Accounting.Account(AccountCodes.SalesRevenue).Id, lines.Single(l => l.IsDebit).AccountId);
    }

    [Fact]
    public void An_entry_can_only_be_reversed_once()
    {
        var original = App.Accounting.Post("بيع", CashSale(80), JournalSource.Manual, Today, null);
        App.Accounting.Reverse(original.Id, Today, null);

        var ex = Assert.Throws<BusinessRuleException>(() => App.Accounting.Reverse(original.Id, Today, null));
        Assert.Contains("تم عكسه", ex.Message);
    }

    [Fact]
    public void Balance_is_reported_in_the_accounts_own_direction()
    {
        App.Accounting.Post("بيع", CashSale(100), JournalSource.Manual, Today, null);
        App.Accounting.Post("مصروف", [
            LedgerLine.Debit("6900", M(30)),
            LedgerLine.Credit(AccountCodes.Cash, M(30)),
        ], JournalSource.Manual, Today, null);

        Assert.Equal(M(70), App.Accounting.BalanceByCode(AccountCodes.Cash));
        Assert.Equal(M(100), App.Accounting.BalanceByCode(AccountCodes.SalesRevenue));
        Assert.Equal(M(30), App.Accounting.BalanceByCode("6900"));
    }

    [Fact]
    public void Every_posting_is_written_to_the_audit_trail()
    {
        var owner = Owner("سالم المالك");
        var entry = App.Accounting.Post("بيع", CashSale(10), JournalSource.Manual, Today, owner);

        var log = Assert.Single(App.Audit.For(nameof(JournalEntry), entry.Id));
        Assert.Equal(AuditAction.Posted, log.Action);
        Assert.Equal("سالم المالك", log.UserName);
        Assert.Contains("JE-2026-000001", log.NewValues);
    }

    // --- Manual entries -------------------------------------------------------

    [Fact]
    public void Only_permitted_users_may_post_manual_entries()
    {
        Assert.Throws<AccessDeniedException>(() =>
            App.Accounting.PostManual(Accountant(), Today, "يدوي", CashSale(10)));

        var entry = App.Accounting.PostManual(Owner(), Today, "يدوي", CashSale(10));
        Assert.Equal(JournalSource.Manual, entry.Source);
    }

    [Fact]
    public void Manual_entries_cannot_post_to_a_deactivated_account()
    {
        RawSql("UPDATE accounts SET is_active = 0 WHERE code = '6900'");

        var ex = Assert.Throws<BusinessRuleException>(() => App.Accounting.PostManual(Owner(), Today, "مصروف", [
            LedgerLine.Debit("6900", M(5)),
            LedgerLine.Credit(AccountCodes.Cash, M(5)),
        ]));

        Assert.Contains("موقوف", ex.Message);
    }

    [Fact]
    public void Document_entries_cannot_be_reversed_by_hand()
    {
        var owner = Owner();
        var saleEntry = App.Accounting.Post("بيع", CashSale(10), JournalSource.Sale, Today, owner);

        var ex = Assert.Throws<BusinessRuleException>(() => App.Accounting.ReverseManual(owner, saleEntry.Id, Today, null));
        Assert.Contains("المستند", ex.Message);

        var manual = App.Accounting.PostManual(owner, Today, "تصحيح", CashSale(10));
        var reversal = App.Accounting.ReverseManual(owner, manual.Id, Today, "خطأ إدخال");
        Assert.Equal("خطأ إدخال", reversal.Memo);
    }

    // --- The database guarantees, proved by bypassing the services ------------

    [Fact]
    public void The_database_refuses_an_entry_inserted_as_already_posted()
    {
        var periodId = App.FiscalPeriods.Containing(Today)!.Id;

        var ex = Assert.Throws<BusinessRuleException>(() => RawSql(
            """
            INSERT INTO journal_entries (entry_number, fiscal_period_id, entry_date, memo, posted_at)
            VALUES ('X-1', @periodId, '2026-06-15', 'مزور', '2026-06-15 10:00:00')
            """,
            new { periodId }));

        Assert.Contains("كمسودة", ex.Message);
    }

    [Fact]
    public void The_database_refuses_to_post_an_unbalanced_draft()
    {
        var periodId = App.FiscalPeriods.Containing(Today)!.Id;
        var cash = App.Accounting.Account(AccountCodes.Cash).Id;
        var revenue = App.Accounting.Account(AccountCodes.SalesRevenue).Id;

        RawSql(
            """
            INSERT INTO journal_entries (id, entry_number, fiscal_period_id, entry_date, memo)
            VALUES (500, 'X-1', @periodId, '2026-06-15', 'مسودة');
            INSERT INTO journal_lines (journal_entry_id, account_id, line_number, debit, credit) VALUES (500, @cash, 1, 10000, 0);
            INSERT INTO journal_lines (journal_entry_id, account_id, line_number, debit, credit) VALUES (500, @revenue, 2, 0, 9999);
            """,
            new { periodId, cash, revenue });

        var ex = Assert.Throws<BusinessRuleException>(() =>
            RawSql("UPDATE journal_entries SET posted_at = '2026-06-15 10:00:00' WHERE id = 500"));

        Assert.Contains("غير متوازن", ex.Message);
    }

    [Fact]
    public void Faked_cached_totals_cannot_be_used_to_post()
    {
        var periodId = App.FiscalPeriods.Containing(Today)!.Id;
        var cash = App.Accounting.Account(AccountCodes.Cash).Id;

        RawSql(
            """
            INSERT INTO journal_entries (id, entry_number, fiscal_period_id, entry_date, memo)
            VALUES (501, 'X-2', @periodId, '2026-06-15', 'مسودة');
            INSERT INTO journal_lines (journal_entry_id, account_id, line_number, debit, credit) VALUES (501, @cash, 1, 10000, 0);
            """,
            new { periodId, cash });

        // Totals claim balance; the lines do not.
        Assert.Throws<BusinessRuleException>(() => RawSql(
            "UPDATE journal_entries SET total_credit = 10000, posted_at = '2026-06-15 10:00:00' WHERE id = 501"));
    }

    [Fact]
    public void A_posted_entry_and_its_lines_are_immutable()
    {
        var entry = App.Accounting.Post("بيع", CashSale(10), JournalSource.Manual, Today, null);

        Assert.Contains("المرحّل", Assert.Throws<BusinessRuleException>(() =>
            RawSql("UPDATE journal_entries SET memo = 'معدل' WHERE id = @id", new { id = entry.Id })).Message);

        Assert.Contains("مرحّل", Assert.Throws<BusinessRuleException>(() =>
            RawSql("DELETE FROM journal_entries WHERE id = @id", new { id = entry.Id })).Message);

        Assert.Contains("مرحّل", Assert.Throws<BusinessRuleException>(() =>
            RawSql("UPDATE journal_lines SET debit = 1 WHERE journal_entry_id = @id AND debit > 0", new { id = entry.Id })).Message);

        Assert.Contains("مرحّل", Assert.Throws<BusinessRuleException>(() =>
            RawSql("DELETE FROM journal_lines WHERE journal_entry_id = @id", new { id = entry.Id })).Message);
    }

    [Fact]
    public void A_line_must_be_one_sided_and_positive()
    {
        var periodId = App.FiscalPeriods.Containing(Today)!.Id;
        var cash = App.Accounting.Account(AccountCodes.Cash).Id;
        RawSql("INSERT INTO journal_entries (id, entry_number, fiscal_period_id, entry_date, memo) VALUES (502, 'X-3', @periodId, '2026-06-15', 'م')", new { periodId });

        Assert.ThrowsAny<Exception>(() => RawSql(
            "INSERT INTO journal_lines (journal_entry_id, account_id, line_number, debit, credit) VALUES (502, @cash, 1, 100, 100)", new { cash }));
        Assert.ThrowsAny<Exception>(() => RawSql(
            "INSERT INTO journal_lines (journal_entry_id, account_id, line_number, debit, credit) VALUES (502, @cash, 1, -100, 0)", new { cash }));
        Assert.ThrowsAny<Exception>(() => RawSql(
            "INSERT INTO journal_lines (journal_entry_id, account_id, line_number, debit, credit) VALUES (502, @cash, 1, 0, 0)", new { cash }));
    }

    [Fact]
    public void An_entry_dated_outside_its_period_is_refused()
    {
        var periodId = App.FiscalPeriods.Containing(Today)!.Id;

        var ex = Assert.Throws<BusinessRuleException>(() => RawSql(
            "INSERT INTO journal_entries (entry_number, fiscal_period_id, entry_date, memo) VALUES ('X-4', @periodId, '2026-07-01', 'م')",
            new { periodId }));

        Assert.Contains("خارج الفترة", ex.Message);
    }

    [Fact]
    public void A_second_reversal_is_refused_by_the_database_itself()
    {
        var original = App.Accounting.Post("بيع", CashSale(10), JournalSource.Manual, Today, null);
        App.Accounting.Reverse(original.Id, Today, null);
        var periodId = App.FiscalPeriods.Containing(Today)!.Id;

        Assert.Throws<BusinessRuleException>(() => RawSql(
            """
            INSERT INTO journal_entries (entry_number, fiscal_period_id, entry_date, memo, reverses_journal_entry_id, is_reversal)
            VALUES ('X-5', @periodId, '2026-06-15', 'عكس ثان', @id, 1)
            """,
            new { periodId, id = original.Id }));
    }

    [Fact]
    public void Writes_into_a_locked_period_are_refused_by_the_database()
    {
        var owner = Owner();
        var entry = App.Accounting.Post("بيع", CashSale(10), JournalSource.Manual, Today, owner);
        var period = App.FiscalPeriods.Containing(Today)!;
        App.FiscalPeriods.Lock(period.Id, owner, null);

        Assert.Contains("مقفلة", Assert.Throws<BusinessRuleException>(() => RawSql(
            "INSERT INTO journal_entries (entry_number, fiscal_period_id, entry_date, memo) VALUES ('X-6', @id, '2026-06-15', 'م')",
            new { id = period.Id })).Message);

        Assert.Contains("مقفلة", Assert.Throws<BusinessRuleException>(() => RawSql(
            "DELETE FROM journal_entries WHERE id = @id", new { id = entry.Id })).Message);
    }

    [Fact]
    public void System_accounts_cannot_be_deleted_or_retyped()
    {
        Assert.Contains("نظامي", Assert.Throws<BusinessRuleException>(() =>
            RawSql("DELETE FROM accounts WHERE code = @code", new { code = AccountCodes.Cash })).Message);

        Assert.Contains("نظامي", Assert.Throws<BusinessRuleException>(() =>
            RawSql("UPDATE accounts SET code = '9999' WHERE code = @code", new { code = AccountCodes.Cash })).Message);
    }

    [Fact]
    public void An_account_with_history_cannot_be_deleted_or_retyped()
    {
        App.Accounting.Post("مصروف", [
            LedgerLine.Debit("6900", M(5)),
            LedgerLine.Credit(AccountCodes.Cash, M(5)),
        ], JournalSource.Manual, Today, null);

        Assert.Throws<BusinessRuleException>(() => RawSql("DELETE FROM accounts WHERE code = '6900'"));
        Assert.Contains("حركات", Assert.Throws<BusinessRuleException>(() =>
            RawSql("UPDATE accounts SET type = 'Asset', normal_balance = 'Debit' WHERE code = '6900'")).Message);
    }

    [Fact]
    public void Normal_balance_must_agree_with_the_account_type()
    {
        Assert.ThrowsAny<Exception>(() => RawSql(
            "INSERT INTO accounts (code, name, type, normal_balance) VALUES ('7777', 'خطأ', 'Asset', 'Credit')"));
    }
}
