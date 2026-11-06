using MotoParts.Core.Common;
using MotoParts.Core.Domain;
using MotoParts.Tests.Support;

namespace MotoParts.Tests.Accounting;

public sealed class FiscalPeriodTests : TestBase
{
    [Fact]
    public void Seeding_creates_monthly_periods_for_this_year_and_next()
    {
        var periods = App.FiscalPeriods.All();

        Assert.Equal(24, periods.Count);
        Assert.Contains(periods, p => p.Name == "2026-02" && p.EndDate == new DateOnly(2026, 2, 28));
        Assert.Contains(periods, p => p.Name == "2027-12");
    }

    [Fact]
    public void Generating_a_year_twice_creates_nothing_the_second_time()
    {
        Assert.Equal(12, App.FiscalPeriods.CreateMonthsForYear(2028, Owner()));
        Assert.Equal(0, App.FiscalPeriods.CreateMonthsForYear(2028, Owner()));
    }

    [Fact]
    public void An_overlapping_period_is_refused_by_the_service_and_the_database()
    {
        var ex = Assert.Throws<BusinessRuleException>(() =>
            App.FiscalPeriods.Create("تداخل", new DateOnly(2026, 6, 20), new DateOnly(2026, 7, 10), Owner()));
        Assert.Contains("تتداخل", ex.Message);

        Assert.Contains("تتداخل", Assert.Throws<BusinessRuleException>(() => RawSql(
            "INSERT INTO fiscal_periods (name, start_date, end_date) VALUES ('خام', '2026-06-20', '2026-07-10')")).Message);
    }

    [Fact]
    public void Only_an_owner_can_lock_and_reopen()
    {
        var period = App.FiscalPeriods.Containing(new DateOnly(2026, 6, 15))!;

        Assert.Throws<AccessDeniedException>(() => App.FiscalPeriods.Lock(period.Id, Accountant(), null));

        var owner = Owner();
        var locked = App.FiscalPeriods.Lock(period.Id, owner, "إقفال الشهر");
        Assert.True(locked.IsLocked);
        Assert.Equal(owner.Id, locked.LockedBy);

        Assert.Throws<AccessDeniedException>(() => App.FiscalPeriods.Unlock(period.Id, Accountant(), "سبب"));
    }

    [Fact]
    public void Reopening_requires_a_reason_and_records_it()
    {
        var owner = Owner("المالك");
        var period = App.FiscalPeriods.Containing(new DateOnly(2026, 6, 15))!;
        App.FiscalPeriods.Lock(period.Id, owner, null);

        Assert.Throws<BusinessRuleException>(() => App.FiscalPeriods.Unlock(period.Id, owner, "  "));

        var reopened = App.FiscalPeriods.Unlock(period.Id, owner, "فاتورة مورد متأخرة");
        Assert.False(reopened.IsLocked);

        var log = App.Audit.For(nameof(FiscalPeriod), period.Id).Single(l => l.Action == AuditAction.Unlocked);
        Assert.Equal("فاتورة مورد متأخرة", log.Reason);
        Assert.Equal("المالك", log.UserName);
    }

    [Fact]
    public void A_locked_period_cannot_be_edited_until_it_is_reopened()
    {
        var owner = Owner();
        var period = App.FiscalPeriods.Containing(new DateOnly(2026, 6, 15))!;
        App.FiscalPeriods.Lock(period.Id, owner, null);

        Assert.Throws<BusinessRuleException>(() =>
            App.FiscalPeriods.Update(period.Id, "اسم جديد", period.StartDate, period.EndDate, owner));
        Assert.Throws<BusinessRuleException>(() =>
            RawSql("UPDATE fiscal_periods SET name = 'خام' WHERE id = @id", new { id = period.Id }));
    }
}
