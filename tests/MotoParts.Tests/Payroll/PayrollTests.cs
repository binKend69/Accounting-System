using MotoParts.Core.Common;
using MotoParts.Core.Domain;
using MotoParts.Core.Security;
using MotoParts.Tests.Support;

namespace MotoParts.Tests.Payroll;

public sealed class PayrollTests : TestBase
{
    public PayrollTests()
    {
        CreateUser(RoleName.Salesperson, "خالد", baseSalary: 4000);
        CreateUser(RoleName.Accountant, "سارة", baseSalary: 6500);
        // No salary: not on the payroll.
        CreateUser(RoleName.Salesperson, "متدرب", baseSalary: 0);
    }

    [Fact]
    public void A_draft_lists_every_active_salaried_employee()
    {
        var run = App.Payroll.Draft(Admin, 2026, 5);

        var lines = App.Payroll.Lines(run.Id);
        Assert.Equal(2, lines.Count);
        Assert.Equal(M(10500), run.TotalNet);
        Assert.Null(run.RunNumber);
    }

    [Fact]
    public void Additions_and_deductions_flow_into_the_totals()
    {
        var run = App.Payroll.Draft(Admin, 2026, 5);
        var khalid = App.Payroll.Lines(run.Id).Single(l => l.EmployeeName == "خالد");

        App.Payroll.UpdateLine(Admin, khalid.Id, M(500), M(200), "عمل إضافي، غياب يوم");

        var updated = App.Payroll.Get(run.Id);
        Assert.Equal(M(500), updated.TotalAdditions);
        Assert.Equal(M(200), updated.TotalDeductions);
        Assert.Equal(M(10800), updated.TotalNet);

        Assert.Throws<BusinessRuleException>(() => App.Payroll.UpdateLine(Admin, khalid.Id, M(0), M(4001), null));
    }

    [Fact]
    public void Posting_accrues_and_paying_settles()
    {
        var run = App.Payroll.Draft(Admin, 2026, 5);

        var posted = App.Payroll.Post(Admin, run.Id);
        Assert.Equal(PayrollStatus.Posted, posted.Status);
        Assert.Equal("PRL-2026-000001", posted.RunNumber);
        Assert.Equal(new DateOnly(2026, 5, 31), posted.PostingDate);
        Assert.Equal(M(10500), Balance(AccountCodes.SalariesExpense));
        Assert.Equal(M(10500), Balance(AccountCodes.SalariesPayable));

        var paid = App.Payroll.Pay(Admin, run.Id, PaymentMethod.Transfer);
        Assert.Equal(PayrollStatus.Paid, paid.Status);
        Assert.Equal(Money.Zero, Balance(AccountCodes.SalariesPayable));
        Assert.Equal(M(-10500), Balance(AccountCodes.Bank));
    }

    [Fact]
    public void A_month_is_paid_once()
    {
        App.Payroll.Draft(Admin, 2026, 5);

        Assert.Contains("من قبل", Assert.Throws<BusinessRuleException>(() => App.Payroll.Draft(Admin, 2026, 5)).Message);
    }

    [Fact]
    public void A_posted_run_is_frozen_by_the_service_and_the_database()
    {
        var run = App.Payroll.Draft(Admin, 2026, 5);
        var line = App.Payroll.Lines(run.Id).First();
        App.Payroll.Post(Admin, run.Id);

        Assert.Throws<BusinessRuleException>(() => App.Payroll.UpdateLine(Admin, line.Id, M(1), M(0), null));
        Assert.Throws<BusinessRuleException>(() => App.Payroll.DeleteDraft(Admin, run.Id));
        Assert.Throws<BusinessRuleException>(() => RawSql("UPDATE payroll_lines SET additions = 1 WHERE id = @id", new { id = line.Id }));
        Assert.Throws<BusinessRuleException>(() => RawSql("UPDATE payroll_runs SET total_net = 1 WHERE id = @id", new { id = run.Id }));
        Assert.Throws<BusinessRuleException>(() => RawSql("UPDATE payroll_runs SET status = 'Draft' WHERE id = @id", new { id = run.Id }));
    }

    [Fact]
    public void A_draft_must_be_posted_before_it_is_paid_and_is_paid_once()
    {
        var run = App.Payroll.Draft(Admin, 2026, 5);

        Assert.Throws<BusinessRuleException>(() => App.Payroll.Pay(Admin, run.Id, PaymentMethod.Cash));

        App.Payroll.Post(Admin, run.Id);
        App.Payroll.Pay(Admin, run.Id, PaymentMethod.Cash);

        Assert.Throws<BusinessRuleException>(() => App.Payroll.Pay(Admin, run.Id, PaymentMethod.Cash));
    }

    [Fact]
    public void Discarding_a_draft_leaves_no_gap_in_the_run_numbers()
    {
        var discarded = App.Payroll.Draft(Admin, 2026, 4);
        App.Payroll.DeleteDraft(Admin, discarded.Id);

        var run = App.Payroll.Post(Admin, App.Payroll.Draft(Admin, 2026, 5).Id);

        Assert.Equal("PRL-2026-000001", run.RunNumber);
    }

    [Fact]
    public void Payroll_is_an_owner_matter()
    {
        Assert.Throws<AccessDeniedException>(() => App.Payroll.Draft(Accountant(), 2026, 5));
    }
}
