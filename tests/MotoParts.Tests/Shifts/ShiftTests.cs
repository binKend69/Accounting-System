using MotoParts.Core.Common;
using MotoParts.Core.Domain;
using MotoParts.Core.Services;
using MotoParts.Tests.Support;

namespace MotoParts.Tests.Shifts;

public sealed class ShiftTests : TestBase
{
    [Fact]
    public void Opening_a_till_records_the_float_and_the_time()
    {
        var cashier = Salesperson();

        var session = OpenTill(cashier, 250);

        Assert.True(session.IsOpen);
        Assert.Equal(M(250), session.OpeningCash);
        Assert.Equal(new DateTime(2026, 6, 15, 10, 0, 0), session.OpenedAt);
    }

    [Fact]
    public void An_employee_cannot_hold_two_open_tills()
    {
        var cashier = Salesperson();
        OpenTill(cashier);

        Assert.Contains("مفتوحة بالفعل", Assert.Throws<BusinessRuleException>(() => OpenTill(cashier)).Message);

        // And the database refuses it even when the check is bypassed.
        Assert.Throws<BusinessRuleException>(() => RawSql(
            "INSERT INTO shift_sessions (shift_id, user_id, opened_at, opening_cash) VALUES (@shift, @user, '2026-06-15 11:00:00', 0)",
            new { shift = MorningShiftId, user = cashier.Id }));
    }

    [Fact]
    public void Closing_short_records_the_difference_and_posts_it()
    {
        var cashier = Salesperson("أحمد");
        var product = Stock("BP-1", 10, 20, 50);
        Tax(0, false);
        var session = OpenTill(cashier, 200);
        App.Sales.Checkout(cashier, [Line(product, 2)], PaymentMethod.Cash);

        Clock.Advance(TimeSpan.FromHours(8));
        var closed = App.Shifts.Close(cashier, session.Id, M(294), "نقص 6");

        Assert.Equal(ShiftSessionStatus.Closed, closed.Status);
        Assert.Equal(M(300), closed.ExpectedCash);
        Assert.Equal(M(294), closed.ActualClosingCash);
        Assert.Equal(M(-6), closed.CashDifference);
        Assert.True(closed.IsShort);

        // Cash on the books now equals the money that exists: 200 float + 100
        // taken - 6 missing (the float was never posted; it is the shop's own cash).
        Assert.Equal(M(94), Balance(AccountCodes.Cash));
        Assert.Equal(M(6), Balance(AccountCodes.CashOverShort));
    }

    [Fact]
    public void Closing_over_posts_the_mirror_entry()
    {
        var cashier = Salesperson();
        var session = OpenTill(cashier, 100);

        App.Shifts.Close(cashier, session.Id, M(105));

        Assert.Equal(M(5), Balance(AccountCodes.Cash));
        Assert.Equal(M(-5), Balance(AccountCodes.CashOverShort));
    }

    [Fact]
    public void A_balanced_close_posts_nothing()
    {
        var cashier = Salesperson();
        var session = OpenTill(cashier, 100);

        App.Shifts.Close(cashier, session.Id, M(100));

        Assert.Equal(0L, RawScalar<long>("SELECT COUNT(*) FROM journal_entries"));
    }

    [Fact]
    public void The_opening_time_survives_closing_and_amending()
    {
        var cashier = Salesperson();
        var session = OpenTill(cashier, 100);

        Clock.Advance(TimeSpan.FromHours(8));
        App.Shifts.Close(cashier, session.Id, M(90));
        Clock.Advance(TimeSpan.FromDays(1));
        var amended = App.Shifts.AmendClosingCash(Owner(), session.Id, M(100), "إعادة عد");

        Assert.Equal(new DateTime(2026, 6, 15, 10, 0, 0), amended.OpenedAt);
        Assert.Equal(new DateTime(2026, 6, 15, 18, 0, 0), amended.ClosedAt);
    }

    [Fact]
    public void Nobody_closes_someone_elses_till_except_an_owner_by_force_with_a_reason()
    {
        var cashier = Salesperson();
        var colleague = Accountant();
        var session = OpenTill(cashier, 100);

        Assert.Throws<AccessDeniedException>(() => App.Shifts.Close(colleague, session.Id, M(100)));
        Assert.Throws<AccessDeniedException>(() => App.Shifts.ForceClose(colleague, session.Id, M(100), "غادر"));

        var owner = Owner("المالك");
        Assert.Throws<BusinessRuleException>(() => App.Shifts.ForceClose(owner, session.Id, M(100), ""));

        var closed = App.Shifts.ForceClose(owner, session.Id, M(95), "غادر دون إغلاق الصندوق");

        Assert.Equal(ShiftSessionStatus.ForceClosed, closed.Status);
        Assert.Equal(owner.Id, closed.ClosedBy);
        Assert.Equal(M(-5), closed.CashDifference);

        var log = App.Audit.For(nameof(ShiftSession), session.Id).Single(l => l.Action == AuditAction.ForceClosed);
        Assert.Equal("المالك", log.UserName);
        Assert.Equal("غادر دون إغلاق الصندوق", log.Reason);
    }

    [Fact]
    public void An_amended_count_posts_only_the_change()
    {
        var cashier = Salesperson();
        var session = OpenTill(cashier, 100);
        App.Shifts.Close(cashier, session.Id, M(90));
        Assert.Equal(M(10), Balance(AccountCodes.CashOverShort));

        var amended = App.Shifts.AmendClosingCash(Owner(), session.Id, M(98), "ورقة عشرة ريالات في الدرج");

        Assert.Equal(M(-2), amended.CashDifference);
        Assert.Equal(M(100), amended.ExpectedCash);
        Assert.Equal(M(2), Balance(AccountCodes.CashOverShort));
        Assert.Equal(M(-2), Balance(AccountCodes.Cash));
    }

    [Fact]
    public void Drawer_vouchers_count_towards_expected_cash()
    {
        var cashier = Accountant();
        var customer = App.Parties.SaveCustomer(null, new CustomerInput("ورشة الأمين", null, null, null, null, M(1000), true, null), cashier);
        App.Parties.SetCustomerOpeningBalance(customer.Id, M(300), Admin);
        var session = OpenTill(cashier, 100);

        App.Vouchers.ReceiveFromCustomer(cashier, customer.Id, M(120), PaymentMethod.Cash, throughDrawer: true);
        App.Vouchers.RecordExpense(cashier, App.Vouchers.ExpenseAccounts().First().Id, M(15), PaymentMethod.Cash,
            throughDrawer: true, "أجرة توصيل");
        // Through the safe: not the drawer's business.
        App.Vouchers.ReceiveFromCustomer(cashier, customer.Id, M(50), PaymentMethod.Cash, throughDrawer: false);

        var totals = App.Shifts.DrawerTotals(session.Id);
        Assert.Equal(M(120), totals.CashReceipts);
        Assert.Equal(M(15), totals.CashPayouts);
        Assert.Equal(M(205), totals.Expected);

        var closed = App.Shifts.Close(cashier, session.Id, M(205));
        Assert.Equal(Money.Zero, closed.CashDifference);
    }

    // --- The database guarantees ------------------------------------------------

    [Fact]
    public void A_closed_till_cannot_be_reopened_or_have_its_snapshot_changed()
    {
        var cashier = Salesperson();
        var session = OpenTill(cashier, 100);
        App.Shifts.Close(cashier, session.Id, M(100));

        // Two triggers refuse this (no reopen; frozen snapshot); either may fire first.
        Assert.Throws<BusinessRuleException>(() => RawSql(
            """
            UPDATE shift_sessions SET status = 'Open', closed_at = NULL, expected_cash = NULL,
                actual_closing_cash = NULL, cash_difference = NULL WHERE id = @id
            """, new { id = session.Id }));
        Assert.False(App.Shifts.Get(session.Id).IsOpen);

        Assert.Throws<BusinessRuleException>(() => RawSql(
            "UPDATE shift_sessions SET expected_cash = 0, cash_difference = 10000 WHERE id = @id", new { id = session.Id }));

        Assert.Throws<BusinessRuleException>(() => RawSql(
            "UPDATE shift_sessions SET opening_cash = 0 WHERE id = @id", new { id = session.Id }));
    }

    [Fact]
    public void The_reconciliation_arithmetic_is_held_by_the_database()
    {
        var cashier = Salesperson();
        var session = OpenTill(cashier, 100);
        App.Shifts.Close(cashier, session.Id, M(100));

        // A difference that does not equal counted - expected is refused.
        Assert.ThrowsAny<Exception>(() => RawSql(
            "UPDATE shift_sessions SET actual_closing_cash = 9000 WHERE id = @id", new { id = session.Id }));
    }

    [Fact]
    public void A_till_session_cannot_be_deleted()
    {
        var session = OpenTill(Salesperson());

        Assert.Throws<BusinessRuleException>(() => RawSql("DELETE FROM shift_sessions WHERE id = @id", new { id = session.Id }));
    }

    // --- Attendance -------------------------------------------------------------

    [Fact]
    public void Checking_in_twice_returns_the_same_record_and_checkout_happens_once()
    {
        var worker = Salesperson();

        var first = App.Shifts.CheckIn(worker);
        Clock.Advance(TimeSpan.FromMinutes(5));
        var second = App.Shifts.CheckIn(worker);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.CheckInAt, second.CheckInAt);

        Clock.Advance(TimeSpan.FromHours(8));
        var outRecord = App.Shifts.CheckOut(worker);
        Assert.Equal(TimeSpan.FromMinutes(485), outRecord.Worked);

        Assert.Throws<BusinessRuleException>(() => App.Shifts.CheckOut(worker));
    }

    [Fact]
    public void Checking_out_without_checking_in_is_refused()
    {
        Assert.Throws<BusinessRuleException>(() => App.Shifts.CheckOut(Salesperson()));
    }
}
