using MotoParts.Core.Common;
using MotoParts.Core.Domain;
using MotoParts.Tests.Support;

namespace MotoParts.Tests.Inventory;

public sealed class InventoryTests : TestBase
{
    [Fact]
    public void Opening_stock_reaches_the_shelf_and_the_ledger_together()
    {
        var product = Stock("BP-1", quantity: 10, unitCost: 18, sellingPrice: 45);

        Assert.Equal(Q(10), product.StockQty);
        Assert.Equal(M(180), product.StockValue);
        Assert.Equal(M(18), product.CostPrice);
        Assert.Equal(M(180), Balance(AccountCodes.Inventory));
        Assert.Equal(M(180), Balance(AccountCodes.OpeningBalanceEquity));
    }

    [Fact]
    public void Issuing_more_than_is_on_hand_is_refused_and_nothing_changes()
    {
        var product = Stock("BP-1", 2, 18, 45);

        var ex = Assert.Throws<InsufficientStockException>(() =>
            App.Inventory.Issue(product.Id, Q(3), StockMovementType.Sale, Admin));

        Assert.Equal(Q(2), ex.Available);
        Assert.Equal(Q(2), App.Catalog.Get(product.Id).StockQty);
        Assert.Single(App.Inventory.MovementsFor(product.Id));
    }

    [Fact]
    public void Receipts_re_average_cost_and_issues_remove_value_at_that_average()
    {
        var product = Stock("OIL-1", 10, 10, 25);

        App.Inventory.Receive(product.Id, Q(10), M(130), StockMovementType.Purchase, Admin);
        var afterReceipt = App.Catalog.Get(product.Id);

        Assert.Equal(M(230), afterReceipt.StockValue);
        Assert.Equal(M(11.50m), afterReceipt.CostPrice);

        var issue = App.Inventory.Issue(product.Id, Q(5), StockMovementType.Sale, Admin);

        Assert.Equal(M(-57.50m), issue.ValueChange);
        Assert.Equal(M(172.50m), App.Catalog.Get(product.Id).StockValue);
    }

    [Fact]
    public void Selling_out_one_unit_at_a_time_removes_exactly_what_was_paid()
    {
        // 10.00 for three units: 3.333... each. Rounding the average and
        // multiplying back out (what the web version did) cannot hit 10.00.
        var product = CreateProduct("NUT-1", 5);
        App.Inventory.Receive(product.Id, Q(3), M(10), StockMovementType.Purchase, Admin);

        var removed = Enumerable.Range(0, 3)
            .Select(_ => -App.Inventory.Issue(product.Id, Q(1), StockMovementType.Sale, Admin).ValueChange)
            .ToList();

        Assert.Equal(M(10), removed.Sum());
        Assert.All(removed, v => Assert.InRange(v.Amount, 3.33m, 3.34m));
        Assert.Equal(Money.Zero, App.Catalog.Get(product.Id).StockValue);
    }

    [Fact]
    public void Fractional_quantities_are_exact()
    {
        var oil = Stock("OIL-L", 20, 12.40m, 20);

        App.Inventory.Issue(oil.Id, Q(2.5m), StockMovementType.Sale, Admin);

        Assert.Equal(Q(17.5m), App.Catalog.Get(oil.Id).StockQty);
        Assert.Equal(M(217), App.Catalog.Get(oil.Id).StockValue);
    }

    [Fact]
    public void A_shortage_adjustment_moves_the_ledger_with_the_shelf()
    {
        var product = Stock("BP-1", 10, 18, 45);

        App.Inventory.Adjust(Admin, product.Id, Q(-2), "قطعتان تالفتان");

        Assert.Equal(Q(8), App.Catalog.Get(product.Id).StockQty);
        Assert.Equal(M(144), Balance(AccountCodes.Inventory));
        Assert.Equal(M(36), Balance(AccountCodes.InventoryAdjustment));
        Assert.Equal(ShelfValue(), Balance(AccountCodes.Inventory));
    }

    [Fact]
    public void Found_stock_is_valued_at_the_current_average()
    {
        var product = Stock("BP-1", 10, 18, 45);

        var movement = App.Inventory.Adjust(Admin, product.Id, Q(1), "وُجدت قطعة في المستودع");

        Assert.Equal(M(18), movement.ValueChange);
        Assert.Equal(M(-18), Balance(AccountCodes.InventoryAdjustment));
        Assert.Equal(ShelfValue(), Balance(AccountCodes.Inventory));
    }

    [Fact]
    public void Found_stock_on_an_empty_shelf_uses_the_last_known_cost()
    {
        var product = Stock("BP-1", 1, 18, 45);
        App.Inventory.Issue(product.Id, Q(1), StockMovementType.Sale, Admin);

        var movement = App.Inventory.Adjust(Admin, product.Id, Q(2), "جرد");

        Assert.Equal(M(36), movement.ValueChange);
    }

    [Fact]
    public void Counting_to_the_same_figure_changes_nothing()
    {
        var product = Stock("BP-1", 10, 18, 45);

        Assert.Null(App.Inventory.AdjustToCount(Admin, product.Id, Q(10), "جرد شهري"));

        var movement = App.Inventory.AdjustToCount(Admin, product.Id, Q(7), "جرد شهري");
        Assert.Equal(Q(-3), movement!.Quantity);
    }

    [Fact]
    public void Adjustments_need_a_reason_and_the_permission()
    {
        var product = Stock("BP-1", 10, 18, 45);

        Assert.Throws<BusinessRuleException>(() => App.Inventory.Adjust(Admin, product.Id, Q(-1), " "));
        Assert.Throws<AccessDeniedException>(() => App.Inventory.Adjust(Salesperson(), product.Id, Q(-1), "سرقة"));
    }

    [Fact]
    public void The_cache_never_drifts_from_the_movement_history()
    {
        var a = Stock("A", 10, 3.33m, 5);
        var b = Stock("B", 7, 1.01m, 2);
        App.Inventory.Issue(a.Id, Q(3), StockMovementType.Sale, Admin);
        App.Inventory.Adjust(Admin, b.Id, Q(-0.5m), "تالف");

        Assert.Empty(App.Inventory.FindDrift());
    }

    // --- The database guarantees ------------------------------------------------

    [Fact]
    public void Stock_cannot_be_edited_directly()
    {
        var product = Stock("BP-1", 10, 18, 45);

        var ex = Assert.Throws<BusinessRuleException>(() =>
            RawSql("UPDATE products SET stock_qty = 999000 WHERE id = @id", new { id = product.Id }));

        Assert.Contains("حركات المخزون", ex.Message);
    }

    [Fact]
    public void A_product_cannot_be_created_with_stock()
    {
        Assert.Throws<BusinessRuleException>(() =>
            RawSql("INSERT INTO products (sku, name, stock_qty, stock_value) VALUES ('X', 'خام', 5000, 100)"));
    }

    [Fact]
    public void A_movement_that_does_not_continue_the_chain_is_refused()
    {
        var product = Stock("BP-1", 10, 18, 45);

        var ex = Assert.Throws<BusinessRuleException>(() => RawSql(
            """
            INSERT INTO stock_movements (product_id, type, quantity, value_change, balance_after, value_after, created_at)
            VALUES (@id, 'Adjustment', 1000, 0, 50000, 18000, '2026-06-15 10:00:00')
            """,
            new { id = product.Id }));

        Assert.Contains("لا تتسلسل", ex.Message);
    }

    [Fact]
    public void Stock_can_never_go_negative_even_by_hand()
    {
        var product = Stock("BP-1", 1, 18, 45);

        Assert.ThrowsAny<Exception>(() => RawSql(
            """
            INSERT INTO stock_movements (product_id, type, quantity, value_change, balance_after, value_after, created_at)
            VALUES (@id, 'Sale', -2000, -1800, -1000, 0, '2026-06-15 10:00:00')
            """,
            new { id = product.Id }));
    }

    [Fact]
    public void A_sale_movement_cannot_add_stock()
    {
        var product = Stock("BP-1", 1, 18, 45);

        Assert.ThrowsAny<Exception>(() => RawSql(
            """
            INSERT INTO stock_movements (product_id, type, quantity, value_change, balance_after, value_after, created_at)
            VALUES (@id, 'Sale', 1000, 1800, 2000, 3600, '2026-06-15 10:00:00')
            """,
            new { id = product.Id }));
    }

    [Fact]
    public void Movements_are_append_only()
    {
        Stock("BP-1", 1, 18, 45);

        Assert.Throws<BusinessRuleException>(() => RawSql("UPDATE stock_movements SET notes = 'x'"));
        Assert.Throws<BusinessRuleException>(() => RawSql("DELETE FROM stock_movements"));
    }

    [Fact]
    public void A_used_product_cannot_be_deleted_only_deactivated()
    {
        var used = Stock("BP-1", 1, 18, 45);
        var unused = CreateProduct("NEW-1", 10);

        Assert.Throws<BusinessRuleException>(() => App.Catalog.DeleteProduct(used.Id, Admin));
        App.Catalog.DeleteProduct(unused.Id, Admin);

        Assert.Throws<BusinessRuleException>(() => App.Catalog.Get(unused.Id));
    }

    [Fact]
    public void Duplicate_codes_and_barcodes_are_refused_with_a_clear_message()
    {
        App.Catalog.CreateProduct(new("BP-1", "590123", "فحمات", null, null, null, null, "طقم", M(45), M(0), Q(0), true), Admin);

        Assert.Contains("رمز الصنف", Assert.Throws<BusinessRuleException>(() =>
            App.Catalog.CreateProduct(new("bp-1", null, "مكرر", null, null, null, null, "قطعة", M(1), M(0), Q(0), true), Admin)).Message);

        Assert.Contains("الباركود", Assert.Throws<BusinessRuleException>(() =>
            App.Catalog.CreateProduct(new("BP-2", "590123", "مكرر", null, null, null, null, "قطعة", M(1), M(0), Q(0), true), Admin)).Message);
    }

    [Fact]
    public void The_till_finds_a_part_by_barcode_sku_or_name()
    {
        App.Catalog.CreateProduct(new("BP-1001", "5901234123457", "فحمات فرامل أمامية", null, null, "Honda", "CG125", "طقم", M(45), M(0), Q(0), true), Admin);

        Assert.NotNull(App.Catalog.FindByCode("5901234123457"));
        Assert.NotNull(App.Catalog.FindByCode("bp-1001"));
        Assert.Single(App.Catalog.SearchForSale("فرامل"));
        Assert.Single(App.Catalog.SearchForSale("CG125"));
    }
}
