using MotoParts.Core.Common;
using MotoParts.Core.Domain;
using MotoParts.Core.Services;
using MotoParts.Tests.Support;

namespace MotoParts.Tests.Sales;

public sealed class RefundTests : TestBase
{
    private readonly Core.Security.Actor _cashier;

    public RefundTests()
    {
        Tax(0.15m, pricesIncludeTax: false);
        _cashier = Accountant("سارة");
        OpenTill(_cashier, 500);
    }

    [Fact]
    public void A_partial_refund_is_its_own_document_and_the_invoice_keeps_its_figures()
    {
        var pads = Stock("BP-1", 10, 18, 45);
        var sale = App.Sales.Checkout(_cashier, [Line(pads, 3)], PaymentMethod.Cash);
        var item = App.Sales.GetItems(sale.Id).Single();

        var refund = App.Sales.Refund(_cashier, sale.Id, [new(item.Id, Q(1))], "مقاس خاطئ");

        Assert.Equal("RFD-2026-000001", refund.RefundNumber);
        Assert.Equal(M(45), refund.Subtotal);
        Assert.Equal(M(6.75m), refund.TaxTotal);
        Assert.Equal(M(51.75m), refund.Total);
        Assert.Equal(M(18), refund.RestockedCogs);

        var after = App.Sales.GetSale(sale.Id);
        Assert.Equal(sale.Total, after.Total);
        Assert.Equal(SaleStatus.PartiallyRefunded, after.Status);
        Assert.Equal(M(51.75m), after.RefundedTotal);

        Assert.Equal(Q(8), App.Catalog.Get(pads.Id).StockQty);
        Assert.Equal(M(45), Balance(AccountCodes.SalesReturns) * -1m);
        Assert.Equal(ShelfValue(), Balance(AccountCodes.Inventory));
        Assert.True(LedgerBalances());
    }

    [Fact]
    public void Refunding_a_line_in_parts_sums_back_to_the_invoice_exactly()
    {
        // Awkward numbers on purpose: thirds of a discounted, taxed line.
        var part = Stock("X-1", 10, 3.33m, 9.99m);
        var sale = App.Sales.Checkout(_cashier, [Line(part, 3, discount: 1.00m)], PaymentMethod.Cash);
        var item = App.Sales.GetItems(sale.Id).Single();

        for (var i = 0; i < 3; i++)
        {
            App.Sales.Refund(_cashier, sale.Id, [new(item.Id, Q(1))], "إرجاع");
        }

        var after = App.Sales.GetSale(sale.Id);
        var refunds = App.Sales.RefundsFor(sale.Id);

        Assert.Equal(SaleStatus.Refunded, after.Status);
        Assert.Equal(sale.Total, refunds.Sum(r => r.Total));
        Assert.Equal(sale.TaxTotal, refunds.Sum(r => r.TaxTotal));
        Assert.Equal(sale.CogsTotal, refunds.Sum(r => r.CogsTotal));
        Assert.Equal(after.Total, after.RefundedTotal);

        // Every halala of the sale has come back out of revenue, tax and cost.
        Assert.Equal(Money.Zero, Balance(AccountCodes.SalesRevenue) + Balance(AccountCodes.SalesReturns));
        Assert.Equal(Money.Zero, Balance(AccountCodes.TaxPayable));
        Assert.Equal(Money.Zero, Balance(AccountCodes.CostOfGoodsSold));
    }

    [Fact]
    public void A_full_refund_restores_everything()
    {
        var pads = Stock("BP-1", 10, 18, 45);
        var oil = Stock("OIL-1", 20, 12.40m, 22);
        var sale = App.Sales.Checkout(_cashier, [Line(pads, 2), Line(oil, 1.5m)], PaymentMethod.Cash);

        App.Sales.RefundInFull(_cashier, sale.Id, "إلغاء الطلب");

        var after = App.Sales.GetSale(sale.Id);
        Assert.Equal(SaleStatus.Refunded, after.Status);
        Assert.Equal(Q(10), App.Catalog.Get(pads.Id).StockQty);
        Assert.Equal(Q(20), App.Catalog.Get(oil.Id).StockQty);
        Assert.Equal(ShelfValue(), Balance(AccountCodes.Inventory));
        Assert.Equal(M(180 + 248), Balance(AccountCodes.Inventory));

        Assert.Throws<BusinessRuleException>(() => App.Sales.RefundInFull(_cashier, sale.Id, "مرة ثانية"));
    }

    [Fact]
    public void Damaged_goods_are_refunded_without_going_back_on_the_shelf()
    {
        var pads = Stock("BP-1", 10, 18, 45);
        var sale = App.Sales.Checkout(_cashier, [Line(pads, 1)], PaymentMethod.Cash);
        var item = App.Sales.GetItems(sale.Id).Single();

        var refund = App.Sales.Refund(_cashier, sale.Id, [new(item.Id, Q(1), Restock: false)], "تالفة");

        Assert.Equal(Money.Zero, refund.RestockedCogs);
        Assert.Equal(Q(9), App.Catalog.Get(pads.Id).StockQty);
        // The cost stays a cost: the part is gone.
        Assert.Equal(M(18), Balance(AccountCodes.CostOfGoodsSold));
        Assert.Equal(ShelfValue(), Balance(AccountCodes.Inventory));
    }

    [Fact]
    public void More_than_was_sold_cannot_be_refunded()
    {
        var pads = Stock("BP-1", 10, 18, 45);
        var sale = App.Sales.Checkout(_cashier, [Line(pads, 2)], PaymentMethod.Cash);
        var item = App.Sales.GetItems(sale.Id).Single();
        App.Sales.Refund(_cashier, sale.Id, [new(item.Id, Q(1))], "إرجاع");

        Assert.Contains("المتبقي", Assert.Throws<BusinessRuleException>(() =>
            App.Sales.Refund(_cashier, sale.Id, [new(item.Id, Q(2))], "إرجاع")).Message);
    }

    [Fact]
    public void A_credit_invoice_is_refunded_against_the_customers_account()
    {
        var pads = Stock("BP-1", 10, 18, 45);
        var garage = App.Parties.SaveCustomer(null, new CustomerInput("ورشة", null, null, null, null, M(1000), true, null), _cashier);
        var sale = App.Sales.Checkout(_cashier, [Line(pads, 2)], PaymentMethod.Credit, customerId: garage.Id);
        var item = App.Sales.GetItems(sale.Id).Single();

        Assert.Throws<BusinessRuleException>(() =>
            App.Sales.Refund(_cashier, sale.Id, [new(item.Id, Q(1))], "إرجاع", PaymentMethod.Cash));

        App.Sales.Refund(_cashier, sale.Id, [new(item.Id, Q(1))], "إرجاع");

        Assert.Equal(M(51.75m), App.Parties.CustomerBalance(garage.Id));
        Assert.Equal(M(51.75m), Balance(AccountCodes.AccountsReceivable));
    }

    [Fact]
    public void A_paid_invoice_cannot_be_refunded_as_a_debt()
    {
        var pads = Stock("BP-1", 10, 18, 45);
        var sale = App.Sales.Checkout(_cashier, [Line(pads, 1)], PaymentMethod.Cash);
        var item = App.Sales.GetItems(sale.Id).Single();

        Assert.Throws<BusinessRuleException>(() =>
            App.Sales.Refund(_cashier, sale.Id, [new(item.Id, Q(1))], "إرجاع", PaymentMethod.Credit));
    }

    [Fact]
    public void A_cash_refund_comes_out_of_the_drawer()
    {
        var pads = Stock("BP-1", 10, 18, 45);
        var session = App.Shifts.OpenSessionFor(_cashier.Id)!;
        var sale = App.Sales.Checkout(_cashier, [Line(pads, 2)], PaymentMethod.Cash);
        App.Sales.RefundInFull(_cashier, sale.Id, "إلغاء");

        var totals = App.Shifts.DrawerTotals(session.Id);

        Assert.Equal(sale.Total, totals.CashSales);
        Assert.Equal(sale.Total, totals.CashRefunds);
        Assert.Equal(M(500), totals.Expected);
    }

    [Fact]
    public void Refunds_need_a_reason_an_open_till_and_the_permission()
    {
        var pads = Stock("BP-1", 10, 18, 45);
        var sale = App.Sales.Checkout(_cashier, [Line(pads, 1)], PaymentMethod.Cash);
        var item = App.Sales.GetItems(sale.Id).Single();

        Assert.Throws<BusinessRuleException>(() => App.Sales.Refund(_cashier, sale.Id, [new(item.Id, Q(1))], " "));
        Assert.Throws<AccessDeniedException>(() => App.Sales.Refund(Salesperson(), sale.Id, [new(item.Id, Q(1))], "سبب"));
        Assert.Throws<NoOpenShiftException>(() => App.Sales.Refund(Accountant(), sale.Id, [new(item.Id, Q(1))], "سبب"));
    }

    [Fact]
    public void Tax_inclusive_refunds_return_the_shelf_price()
    {
        Tax(0.15m, pricesIncludeTax: true);
        var pads = Stock("BP-1", 10, 18, 115);
        var sale = App.Sales.Checkout(_cashier, [Line(pads, 2)], PaymentMethod.Cash);
        var item = App.Sales.GetItems(sale.Id).Single();

        var refund = App.Sales.Refund(_cashier, sale.Id, [new(item.Id, Q(1))], "إرجاع");

        Assert.Equal(M(115), refund.Total);
        Assert.Equal(M(15), refund.TaxTotal);
        Assert.True(LedgerBalances());
    }

    [Fact]
    public void A_refund_document_is_immutable()
    {
        var pads = Stock("BP-1", 10, 18, 45);
        var sale = App.Sales.Checkout(_cashier, [Line(pads, 1)], PaymentMethod.Cash);
        var refund = App.Sales.RefundInFull(_cashier, sale.Id, "إلغاء");

        Assert.Throws<BusinessRuleException>(() => RawSql("UPDATE sale_refunds SET total = 0 WHERE id = @id", new { id = refund.Id }));
        Assert.Throws<BusinessRuleException>(() => RawSql("DELETE FROM sale_refunds WHERE id = @id", new { id = refund.Id }));
        Assert.Throws<BusinessRuleException>(() => RawSql("UPDATE sale_items SET refunded_quantity = 0 WHERE sale_id = @id", new { id = sale.Id }));
    }
}
