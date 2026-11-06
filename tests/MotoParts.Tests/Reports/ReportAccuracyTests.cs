using MotoParts.Core.Common;
using MotoParts.Core.Domain;
using MotoParts.Core.Security;
using MotoParts.Core.Services;
using MotoParts.Tests.Support;

namespace MotoParts.Tests.Reports;

/// <summary>
/// A realistic stretch of trading, then every report checked against it and
/// against each other. If any service ever posted something lopsided, the
/// integrity checks here would catch it.
/// </summary>
public sealed class ReportAccuracyTests : TestBase
{
    private readonly Actor _cashier;
    private readonly Customer _garage;
    private readonly Supplier _supplier;
    private readonly Product _pads;
    private readonly Product _oil;

    public ReportAccuracyTests()
    {
        Tax(0.15m, pricesIncludeTax: false);

        _cashier = CreateUser(RoleName.Accountant, "سارة", baseSalary: 5000);
        _garage = App.Parties.SaveCustomer(null, new CustomerInput("ورشة النجم", null, null, null, null, M(5000), true, null), Admin);
        _supplier = App.Parties.SaveSupplier(null, new SupplierInput("مؤسسة القطع", null, null, null, null, null, true, null), Admin);

        _pads = Stock("BP-1", 10, 18, 45);
        _oil = CreateProduct("OIL-1", 22, unit: "لتر");

        App.Purchases.DraftAndReceive(Admin, new PurchaseInput(_supplier.Id, Today,
            [new PurchaseLineInput(_oil.Id, Q(40), M(9.50m)), new PurchaseLineInput(_pads.Id, Q(5), M(21))],
            PaymentMethod.Credit, 0.15m, "INV-778"));

        var session = OpenTill(_cashier, 300);

        App.Sales.Checkout(_cashier, [Line(_pads, 2), Line(_oil, 4)], PaymentMethod.Cash);
        App.Sales.Checkout(_cashier, [Line(_oil, 1.5m, discount: 3)], PaymentMethod.Card);
        var credit = App.Sales.Checkout(_cashier, [Line(_pads, 3)], PaymentMethod.Credit, customerId: _garage.Id);

        var creditItem = App.Sales.GetItems(credit.Id).Single();
        App.Sales.Refund(_cashier, credit.Id, [new(creditItem.Id, Q(1))], "مقاس خاطئ");

        var cash = App.Sales.Checkout(_cashier, [Line(_oil, 2)], PaymentMethod.Cash);
        App.Sales.RefundInFull(_cashier, cash.Id, "علبة تالفة", restock: false);

        App.Vouchers.ReceiveFromCustomer(_cashier, _garage.Id, M(50), PaymentMethod.Cash, throughDrawer: true);
        App.Vouchers.RecordExpense(_cashier, App.Vouchers.ExpenseAccounts().First().Id, M(12.50m), PaymentMethod.Cash, true, "ضيافة");
        App.Vouchers.PaySupplier(Admin, _supplier.Id, M(200), PaymentMethod.Transfer, false);

        App.Inventory.Adjust(Admin, _pads.Id, Q(-1), "تالفة في المستودع");

        var expected = App.Shifts.DrawerTotals(session.Id).Expected;
        App.Shifts.Close(_cashier, session.Id, expected - M(4.25m));

        App.Payroll.Post(Admin, App.Payroll.Draft(Admin, 2026, 6).Id, Today);
    }

    [Fact]
    public void Every_integrity_check_passes()
    {
        var checks = App.Reports.IntegrityChecks();

        Assert.All(checks, c => Assert.True(c.Passed, $"{c.Name}: {c.Actual} / {c.Expected}"));
    }

    [Fact]
    public void The_trial_balance_ties()
    {
        var trial = App.Reports.TrialBalance();

        Assert.True(trial.IsBalanced);
        Assert.Contains(trial.Rows, r => r.Code == AccountCodes.SalesReturns && r.NetDebit.IsPositive);
    }

    [Fact]
    public void The_balance_sheet_balances_and_includes_the_periods_profit()
    {
        var sheet = App.Reports.BalanceSheet();
        var income = App.Reports.IncomeStatement();

        Assert.True(sheet.IsBalanced, $"{sheet.TotalAssets} vs {sheet.TotalLiabilitiesAndEquity}");
        Assert.Equal(income.NetProfit, sheet.CurrentEarnings);
    }

    [Fact]
    public void The_income_statement_matches_the_ledger_accounts()
    {
        var income = App.Reports.IncomeStatement(Today, Today);

        var revenue = Balance(AccountCodes.SalesRevenue) + Balance(AccountCodes.SalesReturns);
        Assert.Equal(revenue, income.TotalRevenue);
        Assert.Equal(Balance(AccountCodes.CostOfGoodsSold), income.CostOfSales);
        Assert.Equal(M(4.25m), income.Expenses.Single(e => e.Code == AccountCodes.CashOverShort).Amount);
        Assert.Equal(M(5000), income.Expenses.Single(e => e.Code == AccountCodes.SalariesExpense).Amount);
    }

    [Fact]
    public void The_vat_summary_agrees_with_the_ledger()
    {
        var vat = App.Reports.Vat(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30));

        Assert.True(vat.AgreesWithLedger, $"{vat.NetPayable} vs {vat.LedgerNet}");
        Assert.True(vat.PurchasesTax.IsPositive);
        Assert.True(vat.ReturnsTax.IsPositive);
    }

    [Fact]
    public void Sales_by_product_adds_up_to_revenue()
    {
        var byProduct = App.Reports.SalesByProduct(Today, Today);
        var income = App.Reports.IncomeStatement(Today, Today);

        Assert.Equal(income.TotalRevenue, byProduct.Sum(r => r.NetValue));
        Assert.Equal(Q(4), byProduct.Single(r => r.Sku == "BP-1").NetQuantity);
    }

    [Fact]
    public void The_shift_report_shows_the_shortage_and_every_drawer_movement()
    {
        var sessionId = App.Shifts.Sessions(null, null, _cashier.Id).Single().Id;

        var report = App.Reports.ShiftReconciliation(sessionId);

        Assert.Equal(M(-4.25m), report.Session.CashDifference);
        Assert.Equal(4, report.Sales.Count);
        Assert.Equal(2, report.Refunds.Count);
        Assert.Equal(2, report.DrawerVouchers.Count);
        Assert.Equal(report.Totals.Expected, report.Session.ExpectedCash);
    }

    [Fact]
    public void The_cash_report_only_shows_a_restricted_viewer_their_own_tills()
    {
        var other = Salesperson();
        OpenTill(other);

        Assert.Single(App.Reports.CashReconciliation(_cashier, null, null));
        Assert.Equal(2, App.Reports.CashReconciliation(Admin, null, null).Count);
        Assert.Single(App.Reports.CashReconciliation(_cashier, null, null, userId: other.Id));
    }

    [Fact]
    public void The_dashboard_summarises_today()
    {
        var dashboard = App.Reports.Dashboard(Admin);

        Assert.Equal(4, dashboard.Today.InvoiceCount);
        Assert.Equal(1, dashboard.RecentCashDifferences);
        Assert.Equal(App.Parties.CustomerBalance(_garage.Id), dashboard.Receivables);
    }

    [Fact]
    public void A_general_ledger_runs_from_its_opening_balance()
    {
        var cash = App.Accounting.Account(AccountCodes.Cash);

        var ledger = App.Reports.GeneralLedger(cash.Id);

        Assert.Equal(Balance(AccountCodes.Cash), ledger.ClosingBalance);
        Assert.Equal(ledger.ClosingBalance, ledger.Rows[^1].Balance);
    }

    [Fact]
    public void Stock_valuation_matches_the_inventory_account()
    {
        Assert.Equal(Balance(AccountCodes.Inventory), App.Reports.StockValuation().TotalValue);
    }
}
