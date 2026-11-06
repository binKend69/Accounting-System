using MotoParts.Core.Domain;
using MotoParts.Core.Seeding;
using MotoParts.Core.Services;
using MotoParts.Tests.Support;

namespace MotoParts.Tests.Data;

/// <summary>
/// Every list and search query the screens use, run with no filters and with
/// all filters, against demo data. These queries carry many optional
/// parameters, and a parameter bound under the wrong name only fails at runtime.
/// </summary>
public sealed class ListQueryTests : TestBase
{
    public ListQueryTests()
    {
        new DemoDataSeeder(App).Seed(Admin);
    }

    private static readonly DateOnly From = new(2026, 6, 1);
    private static readonly DateOnly To = new(2026, 6, 30);

    [Fact]
    public void Sales_list()
    {
        Assert.NotEmpty(App.Sales.Sales(Admin, new SaleFilter()));
        Assert.NotEmpty(App.Sales.Sales(Admin, new SaleFilter(From, To, null, null, PaymentMethod.Cash, "INV")));
        Assert.NotEmpty(App.Sales.Refunds(Admin, null, null));
        Assert.NotEmpty(App.Sales.Refunds(Admin, From, To));
    }

    [Fact]
    public void Catalogue_and_stock()
    {
        Assert.NotEmpty(App.Catalog.Products());
        Assert.NotEmpty(App.Catalog.Products("فحمات", null, activeOnly: true));
        Assert.NotEmpty(App.Catalog.Categories());
        Assert.NotEmpty(App.Inventory.Movements(null, null));
        Assert.NotEmpty(App.Inventory.Movements(From, To, StockMovementType.Sale, "BP"));
    }

    [Fact]
    public void Parties_and_vouchers()
    {
        Assert.NotEmpty(App.Parties.Customers());
        Assert.NotEmpty(App.Parties.Customers("ورشة", activeOnly: true, withBalanceOnly: true));
        Assert.NotEmpty(App.Parties.Suppliers());
        Assert.NotEmpty(App.Vouchers.CustomerReceipts(null, null));
        Assert.NotEmpty(App.Vouchers.SupplierPayments(From, To));
        Assert.NotEmpty(App.Vouchers.Expenses(null, null));
    }

    [Fact]
    public void Purchasing_shifts_and_accounting()
    {
        Assert.NotEmpty(App.Purchases.Purchases());
        Assert.NotEmpty(App.Purchases.Purchases(From, To, null, PurchaseStatus.Received));
        Assert.NotEmpty(App.Shifts.Sessions(null, null));
        Assert.NotEmpty(App.Shifts.Sessions(From, To, null, differencesOnly: true, ShiftSessionStatus.Closed));
        Assert.NotEmpty(App.Shifts.Attendance(null, null, null));
        Assert.NotEmpty(App.Chart.Accounts());
        Assert.NotEmpty(App.Chart.Entries(null, null));
        Assert.NotEmpty(App.Chart.Entries(From, To, JournalSource.Sale, "INV"));
        Assert.NotEmpty(App.Audit.Search(null, null));
        Assert.NotEmpty(App.Audit.Search(From, To, AuditAction.Posted, nameof(JournalEntry), Admin.Id));
    }
}
