using MotoParts.Core.Common;
using MotoParts.Core.Domain;
using MotoParts.Core.Services;
using MotoParts.Tests.Support;

namespace MotoParts.Tests.Sales;

public sealed class CheckoutTests : TestBase
{
    [Fact]
    public void No_open_till_means_no_sale_and_nothing_written()
    {
        var product = Stock("BP-1", 10, 18, 45);

        Assert.Throws<NoOpenShiftException>(() =>
            App.Sales.Checkout(Salesperson(), [Line(product, 1)], PaymentMethod.Cash));

        Assert.Equal(0L, RawScalar<long>("SELECT COUNT(*) FROM sales"));
        Assert.Equal(Q(10), App.Catalog.Get(product.Id).StockQty);
    }

    [Fact]
    public void A_cash_sale_with_tax_added_on_top_writes_invoice_stock_and_ledger()
    {
        Tax(0.15m, pricesIncludeTax: false);
        var cashier = Salesperson();
        var session = OpenTill(cashier);
        var pads = Stock("BP-1", 10, 18, 45);

        var sale = App.Sales.Checkout(cashier, [Line(pads, 2)], PaymentMethod.Cash, amountTendered: M(110));

        Assert.Equal("INV-2026-000001", sale.InvoiceNumber);
        Assert.Equal(M(90), sale.Subtotal);
        Assert.Equal(M(13.50m), sale.TaxTotal);
        Assert.Equal(M(103.50m), sale.Total);
        Assert.Equal(M(110), sale.AmountPaid);
        Assert.Equal(M(6.50m), sale.ChangeDue);
        Assert.Equal(M(36), sale.CogsTotal);
        Assert.Equal(1500, sale.TaxRateBp);
        Assert.Equal(session.Id, sale.ShiftSessionId);

        Assert.Equal(Q(8), App.Catalog.Get(pads.Id).StockQty);
        Assert.Equal(M(103.50m), Balance(AccountCodes.Cash));
        Assert.Equal(M(90), Balance(AccountCodes.SalesRevenue));
        Assert.Equal(M(13.50m), Balance(AccountCodes.TaxPayable));
        Assert.Equal(M(36), Balance(AccountCodes.CostOfGoodsSold));
        Assert.Equal(ShelfValue(), Balance(AccountCodes.Inventory));
        Assert.True(LedgerBalances());
    }

    [Fact]
    public void With_tax_inclusive_prices_the_customer_pays_the_shelf_price()
    {
        Tax(0.15m, pricesIncludeTax: true);
        var cashier = Salesperson();
        OpenTill(cashier);
        var pads = Stock("BP-1", 10, 18, 115);

        var sale = App.Sales.Checkout(cashier, [Line(pads, 1)], PaymentMethod.Card);

        Assert.True(sale.PricesIncludeTax);
        Assert.Equal(M(115), sale.Total);
        Assert.Equal(M(15), sale.TaxTotal);
        Assert.Equal(M(100), sale.Revenue);
        Assert.Equal(M(115), sale.AmountPaid);
        Assert.Equal(M(115), Balance(AccountCodes.Bank));
        Assert.Equal(M(100), Balance(AccountCodes.SalesRevenue));
    }

    [Fact]
    public void Discounts_reduce_the_line_and_the_tax()
    {
        Tax(0.15m, pricesIncludeTax: false);
        var cashier = Salesperson();
        OpenTill(cashier);
        var pads = Stock("BP-1", 10, 18, 45);

        var sale = App.Sales.Checkout(cashier, [Line(pads, 3, discount: 15)], PaymentMethod.Cash);

        Assert.Equal(M(135), sale.Subtotal);
        Assert.Equal(M(15), sale.DiscountTotal);
        Assert.Equal(M(18), sale.TaxTotal);
        Assert.Equal(M(138), sale.Total);
        Assert.Equal(M(120), App.Sales.GetItems(sale.Id).Single().LineTotal);
    }

    [Fact]
    public void A_discount_larger_than_the_line_is_refused()
    {
        var cashier = Salesperson();
        OpenTill(cashier);
        var pads = Stock("BP-1", 10, 18, 45);

        Assert.Contains("يتجاوز", Assert.Throws<BusinessRuleException>(() =>
            App.Sales.Checkout(cashier, [Line(pads, 1, discount: 46)], PaymentMethod.Cash)).Message);
    }

    [Fact]
    public void An_oversold_line_rolls_back_the_whole_sale_including_its_number()
    {
        var cashier = Salesperson();
        OpenTill(cashier);
        var pads = Stock("BP-1", 10, 18, 45);
        var chain = Stock("CH-1", 1, 60, 120);

        Assert.Throws<InsufficientStockException>(() =>
            App.Sales.Checkout(cashier, [Line(pads, 2), Line(chain, 2)], PaymentMethod.Cash));

        Assert.Equal(0L, RawScalar<long>("SELECT COUNT(*) FROM sales"));
        Assert.Equal(Q(10), App.Catalog.Get(pads.Id).StockQty);

        var sale = App.Sales.Checkout(cashier, [Line(pads, 1)], PaymentMethod.Cash);
        Assert.Equal("INV-2026-000001", sale.InvoiceNumber);
    }

    [Fact]
    public void The_same_part_twice_in_one_cart_is_checked_against_the_combined_quantity()
    {
        var cashier = Salesperson();
        OpenTill(cashier);
        var pads = Stock("BP-1", 3, 18, 45);

        Assert.Throws<InsufficientStockException>(() =>
            App.Sales.Checkout(cashier, [Line(pads, 2), Line(pads, 2)], PaymentMethod.Cash));

        var sale = App.Sales.Checkout(cashier, [Line(pads, 2), Line(pads, 1)], PaymentMethod.Cash);
        Assert.Equal(M(54), sale.CogsTotal);
        Assert.Equal(Q(0), App.Catalog.Get(pads.Id).StockQty);
    }

    [Fact]
    public void Cash_tendered_below_the_total_is_refused()
    {
        var cashier = Salesperson();
        OpenTill(cashier);
        var pads = Stock("BP-1", 3, 18, 45);
        Tax(0, false);

        Assert.Contains("أقل من", Assert.Throws<BusinessRuleException>(() =>
            App.Sales.Checkout(cashier, [Line(pads, 1)], PaymentMethod.Cash, amountTendered: M(40))).Message);
    }

    [Fact]
    public void Credit_sales_need_a_named_customer_within_their_limit()
    {
        Tax(0, false);
        var cashier = Accountant();
        OpenTill(cashier);
        var pads = Stock("BP-1", 10, 18, 45);
        var cashOnly = App.Parties.SaveCustomer(null, new CustomerInput("نقدي", null, null, null, null, M(0), true, null), cashier);
        var garage = App.Parties.SaveCustomer(null, new CustomerInput("ورشة", null, null, null, null, M(100), true, null), cashier);

        Assert.Contains("عميل", Assert.Throws<BusinessRuleException>(() =>
            App.Sales.Checkout(cashier, [Line(pads, 1)], PaymentMethod.Credit)).Message);

        Assert.Contains("نقدي فقط", Assert.Throws<BusinessRuleException>(() =>
            App.Sales.Checkout(cashier, [Line(pads, 1)], PaymentMethod.Credit, customerId: cashOnly.Id)).Message);

        App.Sales.Checkout(cashier, [Line(pads, 2)], PaymentMethod.Credit, customerId: garage.Id);
        Assert.Equal(M(90), App.Parties.CustomerBalance(garage.Id));

        Assert.Contains("حد الائتمان", Assert.Throws<BusinessRuleException>(() =>
            App.Sales.Checkout(cashier, [Line(pads, 1)], PaymentMethod.Credit, customerId: garage.Id)).Message);

        Assert.Equal(M(90), Balance(AccountCodes.AccountsReceivable));
    }

    [Fact]
    public void The_quote_shown_at_the_till_matches_the_invoice_exactly()
    {
        Tax(0.15m, pricesIncludeTax: true);
        var cashier = Salesperson();
        OpenTill(cashier);
        var a = Stock("A", 10, 3.33m, 7.99m);
        var b = Stock("B", 10, 1.11m, 2.35m);
        IReadOnlyList<CartLine> cart = [Line(a, 3, discount: 0.97m), Line(b, 2.5m)];

        var quote = App.Sales.Quote(cart);
        var sale = App.Sales.Checkout(cashier, cart, PaymentMethod.Cash);

        Assert.Equal(quote.Total, sale.Total);
        Assert.Equal(quote.TaxTotal, sale.TaxTotal);
        Assert.Equal(quote.CogsTotal, sale.CogsTotal);
    }

    [Fact]
    public void A_quote_warns_about_shortages_instead_of_failing()
    {
        var pads = Stock("BP-1", 1, 18, 45);

        var quote = App.Sales.Quote([Line(pads, 3)]);

        Assert.Single(quote.Warnings);
    }

    [Fact]
    public void Invoice_numbers_are_gapless_and_yearly()
    {
        var cashier = Salesperson();
        OpenTill(cashier);
        var pads = Stock("BP-1", 10, 18, 45);

        var numbers = Enumerable.Range(0, 3)
            .Select(_ => App.Sales.Checkout(cashier, [Line(pads, 1)], PaymentMethod.Cash).InvoiceNumber)
            .ToList();

        Assert.Equal(["INV-2026-000001", "INV-2026-000002", "INV-2026-000003"], numbers);
    }

    [Fact]
    public void A_second_till_selling_the_last_unit_is_refused()
    {
        // Two scopes = two connections, as two cashier screens would have.
        var first = Salesperson();
        var second = Salesperson();
        OpenTill(first);
        OpenTill(second);
        var lastUnit = Stock("BP-1", 1, 18, 45);

        var tillA = NewScope();
        var tillB = NewScope();

        tillA.Sales.Checkout(first, [Line(lastUnit, 1)], PaymentMethod.Cash);

        Assert.Throws<InsufficientStockException>(() =>
            tillB.Sales.Checkout(second, [Line(lastUnit, 1)], PaymentMethod.Cash));

        Assert.Equal(1L, RawScalar<long>("SELECT COUNT(*) FROM sales"));
    }

    // --- The database guarantees ------------------------------------------------

    [Fact]
    public void An_invoice_cannot_be_edited_or_deleted()
    {
        var cashier = Salesperson();
        OpenTill(cashier);
        var sale = App.Sales.Checkout(cashier, [Line(Stock("BP-1", 10, 18, 45), 1)], PaymentMethod.Cash);

        Assert.Contains("لا تُعدَّل", Assert.Throws<BusinessRuleException>(() =>
            RawSql("UPDATE sales SET total = 1 WHERE id = @id", new { id = sale.Id })).Message);
        Assert.Throws<BusinessRuleException>(() => RawSql("DELETE FROM sales WHERE id = @id", new { id = sale.Id }));
        Assert.Throws<BusinessRuleException>(() => RawSql("UPDATE sale_items SET unit_price = 1 WHERE sale_id = @id", new { id = sale.Id }));
        Assert.Throws<BusinessRuleException>(() => RawSql("DELETE FROM sale_items WHERE sale_id = @id", new { id = sale.Id }));
    }

    [Fact]
    public void A_sale_outside_the_sellers_open_till_is_refused_by_the_database()
    {
        var cashier = Salesperson();
        var other = Salesperson();
        var othersTill = OpenTill(other);

        var ex = Assert.Throws<BusinessRuleException>(() => RawSql(
            """
            INSERT INTO sales (invoice_number, sale_date, sold_at, user_id, shift_session_id, prices_include_tax, tax_rate_bp,
                               subtotal, total, payment_method, amount_paid)
            VALUES ('X-1', '2026-06-15', '2026-06-15 10:00:00', @user, @session, 0, 0, 100, 100, 'Cash', 100)
            """,
            new { user = cashier.Id, session = othersTill.Id }));

        Assert.Contains("وردية مفتوحة", ex.Message);
    }

    [Fact]
    public void Invoice_arithmetic_is_checked_by_the_database()
    {
        var cashier = Salesperson();
        var till = OpenTill(cashier);

        // total must equal subtotal - discount + tax (tax-exclusive invoice)
        Assert.ThrowsAny<Exception>(() => RawSql(
            """
            INSERT INTO sales (invoice_number, sale_date, sold_at, user_id, shift_session_id, prices_include_tax, tax_rate_bp,
                               subtotal, tax_total, total, payment_method, amount_paid)
            VALUES ('X-1', '2026-06-15', '2026-06-15 10:00:00', @user, @session, 0, 1500, 10000, 1500, 9999, 'Cash', 9999)
            """,
            new { user = cashier.Id, session = till.Id }));
    }
}
