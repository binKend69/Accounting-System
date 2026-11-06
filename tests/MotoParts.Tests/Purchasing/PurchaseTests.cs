using MotoParts.Core.Common;
using MotoParts.Core.Domain;
using MotoParts.Core.Services;
using MotoParts.Tests.Support;

namespace MotoParts.Tests.Purchasing;

public sealed class PurchaseTests : TestBase
{
    private Supplier NewSupplier(string name = "مؤسسة القطع") =>
        App.Parties.SaveSupplier(null, new SupplierInput(name, null, null, null, null, null, true, null), Admin);

    private PurchaseInput Order(long supplierId, params (Product Product, decimal Qty, decimal Cost)[] lines) =>
        new(supplierId, new DateOnly(2026, 6, 15),
            lines.Select(l => new PurchaseLineInput(l.Product.Id, Q(l.Qty), M(l.Cost))).ToList(),
            PaymentMethod.Credit, TaxRate: 0.15m, SupplierInvoiceNumber: "S-889");

    [Fact]
    public void A_draft_is_priced_but_moves_nothing()
    {
        var supplier = NewSupplier();
        var pads = CreateProduct("BP-1", 45);

        var draft = App.Purchases.Draft(Admin, Order(supplier.Id, (pads, 10, 18)));

        Assert.Equal("PO-2026-000001", draft.PurchaseNumber);
        Assert.Equal(PurchaseStatus.Draft, draft.Status);
        Assert.Equal(M(180), draft.Subtotal);
        Assert.Equal(M(27), draft.TaxTotal);
        Assert.Equal(M(207), draft.Total);
        Assert.Equal(Q(0), App.Catalog.Get(pads.Id).StockQty);
        Assert.Equal(0L, RawScalar<long>("SELECT COUNT(*) FROM journal_entries"));
    }

    [Fact]
    public void Receiving_brings_stock_in_and_posts_inventory_tax_and_the_payable()
    {
        var supplier = NewSupplier();
        var pads = CreateProduct("BP-1", 45);
        var draft = App.Purchases.Draft(Admin, Order(supplier.Id, (pads, 10, 18)));

        var received = App.Purchases.Receive(Admin, draft.Id);

        Assert.Equal(PurchaseStatus.Received, received.Status);
        Assert.Equal(Q(10), App.Catalog.Get(pads.Id).StockQty);
        Assert.Equal(M(18), App.Catalog.Get(pads.Id).CostPrice);
        Assert.Equal(M(180), Balance(AccountCodes.Inventory));
        Assert.Equal(M(-27), Balance(AccountCodes.TaxPayable));
        Assert.Equal(M(207), Balance(AccountCodes.AccountsPayable));
        Assert.Equal(M(207), App.Parties.SupplierBalance(supplier.Id));
        Assert.Equal(ShelfValue(), Balance(AccountCodes.Inventory));
    }

    [Fact]
    public void Cost_is_averaged_across_purchases_at_different_prices()
    {
        var supplier = NewSupplier();
        var pads = CreateProduct("BP-1", 45);

        App.Purchases.DraftAndReceive(Admin, Order(supplier.Id, (pads, 10, 18)));
        App.Purchases.DraftAndReceive(Admin, Order(supplier.Id, (pads, 5, 21)));

        var product = App.Catalog.Get(pads.Id);
        Assert.Equal(M(285), product.StockValue);
        Assert.Equal(M(19), product.CostPrice);
        Assert.Equal(ShelfValue(), Balance(AccountCodes.Inventory));
    }

    [Fact]
    public void A_cash_purchase_credits_cash_not_the_supplier()
    {
        var supplier = NewSupplier();
        var pads = CreateProduct("BP-1", 45);

        App.Purchases.DraftAndReceive(Admin, Order(supplier.Id, (pads, 2, 18)) with { PaymentMethod = PaymentMethod.Cash, TaxRate = 0 });

        Assert.Equal(M(-36), Balance(AccountCodes.Cash));
        Assert.Equal(Money.Zero, App.Parties.SupplierBalance(supplier.Id));
    }

    [Fact]
    public void A_purchase_is_received_once_and_only_drafts_are_cancelled()
    {
        var supplier = NewSupplier();
        var pads = CreateProduct("BP-1", 45);
        var draft = App.Purchases.Draft(Admin, Order(supplier.Id, (pads, 10, 18)));
        App.Purchases.Receive(Admin, draft.Id);

        Assert.Contains("مستلم", Assert.Throws<BusinessRuleException>(() => App.Purchases.Receive(Admin, draft.Id)).Message);
        Assert.Throws<BusinessRuleException>(() => App.Purchases.Cancel(Admin, draft.Id, "خطأ"));

        // The database freezes it as well.
        Assert.Throws<BusinessRuleException>(() => RawSql("UPDATE purchases SET total = 0 WHERE id = @id", new { id = draft.Id }));
        Assert.Throws<BusinessRuleException>(() => RawSql("DELETE FROM purchase_items WHERE purchase_id = @id", new { id = draft.Id }));
    }

    [Fact]
    public void A_cancelled_draft_keeps_its_number_and_cannot_be_received()
    {
        var supplier = NewSupplier();
        var pads = CreateProduct("BP-1", 45);
        var draft = App.Purchases.Draft(Admin, Order(supplier.Id, (pads, 10, 18)));

        Assert.Throws<BusinessRuleException>(() => App.Purchases.Cancel(Admin, draft.Id, " "));
        var cancelled = App.Purchases.Cancel(Admin, draft.Id, "المورد لم يورّد");

        Assert.Equal(PurchaseStatus.Cancelled, cancelled.Status);
        Assert.Equal("المورد لم يورّد", cancelled.CancelReason);
        Assert.Throws<BusinessRuleException>(() => App.Purchases.Receive(Admin, draft.Id));
        Assert.Throws<BusinessRuleException>(() => RawSql("DELETE FROM purchases WHERE id = @id", new { id = draft.Id }));
    }

    [Fact]
    public void A_draft_can_be_rewritten_before_it_is_received()
    {
        var supplier = NewSupplier();
        var pads = CreateProduct("BP-1", 45);
        var oil = CreateProduct("OIL-1", 22);
        var draft = App.Purchases.Draft(Admin, Order(supplier.Id, (pads, 10, 18)));

        var updated = App.Purchases.UpdateDraft(Admin, draft.Id, Order(supplier.Id, (pads, 4, 18), (oil, 12, 9.50m)));

        Assert.Equal(2, App.Purchases.Items(draft.Id).Count);
        Assert.Equal(M(186), updated.Subtotal);
    }

    [Fact]
    public void Purchasing_needs_the_permission()
    {
        var supplier = NewSupplier();
        var pads = CreateProduct("BP-1", 45);

        Assert.Throws<AccessDeniedException>(() => App.Purchases.Draft(Salesperson(), Order(supplier.Id, (pads, 1, 18))));
    }
}
