using MotoParts.Core.Common;
using MotoParts.Core.Domain;
using MotoParts.Core.Services;
using MotoParts.Tests.Support;

namespace MotoParts.Tests.Vouchers;

public sealed class VoucherTests : TestBase
{
    private Customer GarageOwing(decimal amount)
    {
        var customer = App.Parties.SaveCustomer(null, new CustomerInput("ورشة النجم", "0500000000", null, null, null, M(5000), true, null), Admin);
        App.Parties.SetCustomerOpeningBalance(customer.Id, M(amount), Admin);
        return customer;
    }

    private Supplier SupplierOwed(decimal amount)
    {
        var supplier = App.Parties.SaveSupplier(null, new SupplierInput("مؤسسة القطع", null, null, null, null, null, true, null), Admin);
        App.Parties.SetSupplierOpeningBalance(supplier.Id, M(amount), Admin);
        return supplier;
    }

    [Fact]
    public void A_customer_receipt_settles_the_account_and_the_ledger()
    {
        var customer = GarageOwing(800);

        var receipt = App.Vouchers.ReceiveFromCustomer(Admin, customer.Id, M(300), PaymentMethod.Transfer, throughDrawer: false, "حوالة 4471");

        Assert.Equal("RCV-2026-000001", receipt.ReceiptNumber);
        Assert.Equal(M(500), App.Parties.CustomerBalance(customer.Id));
        Assert.Equal(M(500), Balance(AccountCodes.AccountsReceivable));
        Assert.Equal(M(300), Balance(AccountCodes.Bank));
    }

    [Fact]
    public void A_receipt_cannot_exceed_what_the_customer_owes()
    {
        var customer = GarageOwing(100);

        Assert.Contains("أكبر من رصيد", Assert.Throws<BusinessRuleException>(() =>
            App.Vouchers.ReceiveFromCustomer(Admin, customer.Id, M(100.01m), PaymentMethod.Cash, false)).Message);
    }

    [Fact]
    public void The_opening_balance_is_posted_once()
    {
        var customer = GarageOwing(100);

        Assert.Throws<BusinessRuleException>(() => App.Parties.SetCustomerOpeningBalance(customer.Id, M(50), Admin));
        Assert.Throws<BusinessRuleException>(() =>
            RawSql("UPDATE customers SET opening_balance = 1 WHERE id = @id", new { id = customer.Id }));
    }

    [Fact]
    public void A_supplier_payment_reduces_what_is_owed()
    {
        var supplier = SupplierOwed(1000);

        App.Vouchers.PaySupplier(Admin, supplier.Id, M(400), PaymentMethod.Transfer, false);

        Assert.Equal(M(600), App.Parties.SupplierBalance(supplier.Id));
        Assert.Equal(M(600), Balance(AccountCodes.AccountsPayable));
        Assert.Equal(M(-400), Balance(AccountCodes.Bank));
        Assert.Throws<BusinessRuleException>(() => App.Vouchers.PaySupplier(Admin, supplier.Id, M(601), PaymentMethod.Cash, false));
    }

    [Fact]
    public void An_expense_voucher_charges_the_chosen_expense_account()
    {
        var rent = App.Vouchers.ExpenseAccounts().Single(a => a.Code == "6300");

        var voucher = App.Vouchers.RecordExpense(Admin, rent.Id, M(3000), PaymentMethod.Transfer, false, "إيجار يونيو", "مالك العقار");

        Assert.Equal("EXP-2026-000001", voucher.VoucherNumber);
        Assert.Equal(M(3000), App.Accounting.Balance(rent.Id));
        Assert.Equal(M(-3000), Balance(AccountCodes.Bank));
    }

    [Fact]
    public void System_driven_expense_accounts_are_not_offered_or_accepted()
    {
        var accounts = App.Vouchers.ExpenseAccounts();
        Assert.DoesNotContain(accounts, a => a.Code == AccountCodes.CostOfGoodsSold);

        var cogs = App.Accounting.Account(AccountCodes.CostOfGoodsSold);
        Assert.Throws<BusinessRuleException>(() =>
            App.Vouchers.RecordExpense(Admin, cogs.Id, M(10), PaymentMethod.Cash, false, "محاولة"));

        var cash = App.Accounting.Account(AccountCodes.Cash);
        Assert.Throws<BusinessRuleException>(() =>
            App.Vouchers.RecordExpense(Admin, cash.Id, M(10), PaymentMethod.Cash, false, "ليس مصروفاً"));
    }

    [Fact]
    public void Vouchers_are_settled_now_never_on_credit()
    {
        var customer = GarageOwing(100);

        Assert.Throws<BusinessRuleException>(() =>
            App.Vouchers.ReceiveFromCustomer(Admin, customer.Id, M(10), PaymentMethod.Credit, false));
    }

    [Fact]
    public void Voiding_posts_a_reversal_and_keeps_the_voucher_on_record()
    {
        var customer = GarageOwing(800);
        var receipt = App.Vouchers.ReceiveFromCustomer(Admin, customer.Id, M(300), PaymentMethod.Transfer, false);

        var voided = App.Vouchers.VoidCustomerReceipt(Admin, receipt.Id, "حوالة مرتجعة من البنك");

        Assert.True(voided.IsVoided);
        Assert.Equal("حوالة مرتجعة من البنك", voided.VoidReason);
        Assert.Equal(M(800), App.Parties.CustomerBalance(customer.Id));
        Assert.Equal(M(800), Balance(AccountCodes.AccountsReceivable));
        Assert.Equal(Money.Zero, Balance(AccountCodes.Bank));

        Assert.Throws<BusinessRuleException>(() => App.Vouchers.VoidCustomerReceipt(Admin, receipt.Id, "مرة ثانية"));
    }

    [Fact]
    public void Only_permitted_users_may_void()
    {
        var customer = GarageOwing(800);
        var cashier = Accountant();
        var receipt = App.Vouchers.ReceiveFromCustomer(cashier, customer.Id, M(300), PaymentMethod.Card, false);

        Assert.Throws<AccessDeniedException>(() => App.Vouchers.VoidCustomerReceipt(cashier, receipt.Id, "خطأ"));
    }

    [Fact]
    public void A_drawer_voucher_cannot_be_voided_after_its_till_closed()
    {
        var customer = GarageOwing(800);
        var session = OpenTill(Admin, 100);
        var receipt = App.Vouchers.ReceiveFromCustomer(Admin, customer.Id, M(50), PaymentMethod.Cash, throughDrawer: true);
        App.Shifts.Close(Admin, session.Id, M(150));

        Assert.Contains("إغلاقها", Assert.Throws<BusinessRuleException>(() =>
            App.Vouchers.VoidCustomerReceipt(Admin, receipt.Id, "خطأ")).Message);
    }

    [Fact]
    public void Drawer_vouchers_need_cash_and_an_open_till()
    {
        var customer = GarageOwing(800);

        Assert.Throws<NoOpenShiftException>(() =>
            App.Vouchers.ReceiveFromCustomer(Admin, customer.Id, M(10), PaymentMethod.Cash, throughDrawer: true));

        OpenTill(Admin);
        Assert.Throws<BusinessRuleException>(() =>
            App.Vouchers.ReceiveFromCustomer(Admin, customer.Id, M(10), PaymentMethod.Card, throughDrawer: true));
    }

    [Fact]
    public void Vouchers_are_immutable_in_the_database()
    {
        var customer = GarageOwing(800);
        var receipt = App.Vouchers.ReceiveFromCustomer(Admin, customer.Id, M(300), PaymentMethod.Card, false);

        Assert.Throws<BusinessRuleException>(() => RawSql("UPDATE customer_receipts SET amount = 1 WHERE id = @id", new { id = receipt.Id }));
        Assert.Throws<BusinessRuleException>(() => RawSql("DELETE FROM customer_receipts WHERE id = @id", new { id = receipt.Id }));
    }

    [Fact]
    public void A_customer_statement_runs_from_the_opening_balance()
    {
        Tax(0, false);
        var customer = GarageOwing(100);
        OpenTill(Admin);
        var pads = Stock("BP-1", 10, 18, 45);
        App.Sales.Checkout(Admin, [Line(pads, 2)], PaymentMethod.Credit, customerId: customer.Id);
        App.Vouchers.ReceiveFromCustomer(Admin, customer.Id, M(60), PaymentMethod.Cash, false);

        var statement = App.Parties.CustomerStatement(customer.Id);

        Assert.Equal(M(100), statement[0].Balance);
        Assert.Equal(M(190), statement[1].Balance);
        Assert.Equal(M(130), statement[^1].Balance);
        Assert.Equal(App.Parties.CustomerBalance(customer.Id), statement[^1].Balance);
    }
}
