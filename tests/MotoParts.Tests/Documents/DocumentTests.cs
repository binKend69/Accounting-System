using QuestPDF.Fluent;
using QuestPDF.Infrastructure;
using MotoParts.Core.Domain;
using MotoParts.Core.Security;
using MotoParts.Core.Services;
using MotoParts.Documents;
using MotoParts.Tests.Support;

namespace MotoParts.Tests.Documents;

/// <summary>
/// Every printable document, generated from real data. Set the environment
/// variable MOTOPARTS_DOC_PREVIEW to a folder to also get PNG previews of each
/// page for a visual check.
/// </summary>
public sealed class DocumentTests : TestBase
{
    private readonly DocumentFactory _documents;
    private readonly Actor _cashier;
    private readonly long _saleId;
    private readonly long _creditSaleId;
    private readonly long _refundId;
    private readonly long _sessionId;
    private readonly long _receiptId;
    private readonly long _paymentId;
    private readonly long _expenseId;
    private readonly long _runId;
    private readonly long _customerId;
    private readonly long _supplierId;

    public DocumentTests()
    {
        App.Settings.SaveShopProfile(new ShopProfile("مؤسسة الدراجة السريعة لقطع الغيار", "الرياض - حي العزيزية - شارع الخرج",
            "0112345678", "300123456700003", "1010123456", "ر.س", "شكراً لتعاملكم معنا. الاستبدال خلال 7 أيام مع الفاتورة."), Admin);
        Tax(0.15m, pricesIncludeTax: true);

        _cashier = CreateUser(RoleName.Accountant, "سارة العتيبي", baseSalary: 5500);
        var customer = App.Parties.SaveCustomer(null, new CustomerInput("ورشة النجم للصيانة", "0551234567", null, "الرياض", "310987654300003", M(5000), true, null), Admin);
        var supplier = App.Parties.SaveSupplier(null, new SupplierInput("شركة القطع الأصلية", "أبو خالد", "0509876543", null, null, null, true, null), Admin);
        _customerId = customer.Id;
        _supplierId = supplier.Id;

        var pads = App.Catalog.CreateProduct(new ProductInput("BP-1001", "5901234123457", "فحمات فرامل أمامية هوندا", null, null, "Honda", "CG125 2004-2012", "طقم", M(115), M(0), Q(5), true), Admin);
        var oil = App.Catalog.CreateProduct(new ProductInput("OIL-10W40", null, "زيت محرك 10W-40 شبه صناعي", null, null, "Motul", null, "لتر", M(34.50m), M(0), Q(10), true), Admin);
        App.Purchases.DraftAndReceive(Admin, new PurchaseInput(supplier.Id, Today,
            [new PurchaseLineInput(pads.Id, Q(12), M(55)), new PurchaseLineInput(oil.Id, Q(24), M(18.25m))], PaymentMethod.Credit, 0.15m, "SUP-5521"));

        var session = OpenTill(_cashier, 500);
        _sessionId = session.Id;

        _saleId = App.Sales.Checkout(_cashier, [Line(pads, 2, discount: 10), Line(oil, 3.5m)], PaymentMethod.Cash, amountTendered: M(500)).Id;
        _creditSaleId = App.Sales.Checkout(_cashier, [Line(pads, 3)], PaymentMethod.Credit, customerId: customer.Id).Id;
        var item = App.Sales.GetItems(_creditSaleId).Single();
        _refundId = App.Sales.Refund(_cashier, _creditSaleId, [new(item.Id, Q(1))], "القطعة لا تناسب الموديل").Id;

        _receiptId = App.Vouchers.ReceiveFromCustomer(_cashier, customer.Id, M(150), PaymentMethod.Cash, true, "دفعة").Id;
        _expenseId = App.Vouchers.RecordExpense(_cashier, App.Vouchers.ExpenseAccounts().First().Id, M(1250.75m), PaymentMethod.Transfer, false, "إيجار شهر يونيو", "مكتب العقار").Id;
        _paymentId = App.Vouchers.PaySupplier(Admin, supplier.Id, M(500), PaymentMethod.Transfer, false, "حوالة 7781").Id;

        App.Shifts.Close(_cashier, _sessionId, App.Shifts.DrawerTotals(_sessionId).Expected - M(3.5m));
        _runId = App.Payroll.Post(Admin, App.Payroll.Draft(Admin, 2026, 6).Id, Today).Id;

        _documents = new DocumentFactory(App);
    }

    public static TheoryData<string> DocumentNames() =>
    [
        "invoice-a4", "invoice-credit-a4", "invoice-receipt", "refund", "shift", "receipt", "payment", "expense",
        "trial-balance", "general-ledger", "statements", "vat", "cash-reconciliation", "low-stock", "stock-list",
        "sales-by-product", "sales-by-employee", "customer-statement", "supplier-statement", "payroll", "integrity",
    ];

    [Theory]
    [MemberData(nameof(DocumentNames))]
    public void Every_document_renders(string name)
    {
        var document = Build(name);

        var pdf = DocumentFactory.ToPdf(document);

        Assert.True(pdf.Length > 1000);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));

        var preview = Environment.GetEnvironmentVariable("MOTOPARTS_DOC_PREVIEW");

        if (!string.IsNullOrWhiteSpace(preview))
        {
            Directory.CreateDirectory(preview);
            File.WriteAllBytes(Path.Combine(preview, $"{name}.pdf"), pdf);

            var page = 0;

            foreach (var image in document.GenerateImages(new ImageGenerationSettings { RasterDpi = 110 }))
            {
                File.WriteAllBytes(Path.Combine(preview, $"{name}-{++page}.png"), image);
            }
        }
    }

    private IDocument Build(string name)
    {
        var month = (new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30));
        var cash = App.Accounting.Account(AccountCodes.Cash).Id;

        return name switch
        {
            "invoice-a4" => _documents.Invoice(_saleId),
            "invoice-credit-a4" => _documents.Invoice(_creditSaleId),
            "invoice-receipt" => _documents.Invoice(_saleId, InvoiceFormat.Receipt80mm),
            "refund" => _documents.Refund(_refundId),
            "shift" => _documents.ShiftReport(_sessionId),
            "receipt" => _documents.CustomerReceipt(_receiptId),
            "payment" => _documents.SupplierPayment(_paymentId),
            "expense" => _documents.Expense(_expenseId),
            "trial-balance" => _documents.TrialBalance(null, null),
            "general-ledger" => _documents.GeneralLedger(cash, month.Item1, month.Item2),
            "statements" => _documents.FinancialStatements(month.Item1, month.Item2),
            "vat" => _documents.Vat(month.Item1, month.Item2),
            "cash-reconciliation" => _documents.CashReconciliation(Admin, month.Item1, month.Item2, null),
            "low-stock" => _documents.LowStock(),
            "stock-list" => _documents.StockList(),
            "sales-by-product" => _documents.SalesByProduct(month.Item1, month.Item2),
            "sales-by-employee" => _documents.SalesByEmployee(month.Item1, month.Item2),
            "customer-statement" => _documents.CustomerStatement(_customerId, null, null),
            "supplier-statement" => _documents.SupplierStatement(_supplierId, null, null),
            "payroll" => _documents.PayrollSheet(_runId),
            "integrity" => _documents.IntegrityReport(),
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };
    }
}
