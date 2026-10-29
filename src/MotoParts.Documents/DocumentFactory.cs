using QuestPDF.Fluent;
using QuestPDF.Infrastructure;
using MotoParts.Core;
using MotoParts.Core.Common;
using MotoParts.Core.Domain;
using MotoParts.Core.Security;
using MotoParts.Core.Services;

namespace MotoParts.Documents;

/// <summary>
/// Builds every printable document from the database. The screens ask for "the
/// invoice for sale 42" and get a document; they never assemble one.
/// </summary>
public sealed class DocumentFactory
{
    private readonly ServiceScope _app;

    public DocumentFactory(ServiceScope app)
    {
        DocumentTheme.Configure();
        _app = app;
    }

    private ShopProfile Shop => _app.Settings.GetShopProfile();

    public static void SavePdf(IDocument document, string path) => document.GeneratePdf(path);

    public static byte[] ToPdf(IDocument document) => document.GeneratePdf();

    // --- Operational documents ------------------------------------------------------

    public IDocument Invoice(long saleId, InvoiceFormat format = InvoiceFormat.A4)
    {
        var sale = _app.Sales.GetSale(saleId);

        var data = new InvoiceData(
            Shop,
            sale,
            _app.Sales.GetItems(saleId),
            _app.Sales.RefundsFor(saleId),
            _app.Users.Get(sale.UserId).FullName,
            sale.CustomerId is { } customerId ? _app.Parties.GetCustomer(customerId) : null);

        return new InvoiceDocument(data, format);
    }

    public IDocument Refund(long refundId)
    {
        var refund = _app.Sales.GetRefund(refundId);
        var sale = _app.Sales.GetSale(refund.SaleId);
        var items = _app.Sales.GetItems(sale.Id).ToDictionary(i => i.Id);

        var lines = _app.Sales.GetRefundItems(refundId)
            .Select(l => (l, items[l.SaleItemId]))
            .ToList();

        return new RefundDocument(new RefundData(
            Shop, refund, sale, lines,
            _app.Users.Get(refund.UserId).FullName,
            sale.CustomerId is { } customerId ? _app.Parties.GetCustomer(customerId) : null));
    }

    public IDocument ShiftReport(long sessionId) =>
        new ShiftReportDocument(Shop, _app.Reports.ShiftReconciliation(sessionId));

    public IDocument CustomerReceipt(long id)
    {
        var r = _app.Vouchers.GetCustomerReceipt(id);

        return new VoucherDocument(new VoucherData(
            Shop, "سند قبض", r.ReceiptNumber, r.ReceivedAt, "استلمنا من", _app.Parties.GetCustomer(r.CustomerId).Name,
            r.Amount, r.PaymentMethod, r.Notes ?? "سداد من الحساب", r.Reference, _app.Users.Get(r.UserId).FullName,
            r.IsVoided, r.VoidReason, ["المستلم", "المحاسب"]));
    }

    public IDocument SupplierPayment(long id)
    {
        var p = _app.Vouchers.GetSupplierPayment(id);

        return new VoucherDocument(new VoucherData(
            Shop, "سند صرف", p.PaymentNumber, p.PaidAt, "صرفنا إلى", _app.Parties.GetSupplier(p.SupplierId).Name,
            p.Amount, p.PaymentMethod, p.Notes ?? "سداد مستحقات", p.Reference, _app.Users.Get(p.UserId).FullName,
            p.IsVoided, p.VoidReason, ["المستلم", "المحاسب", "المالك"]));
    }

    public IDocument Expense(long id)
    {
        var v = _app.Vouchers.GetExpense(id);
        var account = _app.Accounting.GetAccountById(v.ExpenseAccountId);

        return new VoucherDocument(new VoucherData(
            Shop, "سند صرف مصروف", v.VoucherNumber, v.PaidAt, "صرفنا إلى", v.Payee ?? account.Name,
            v.Amount, v.PaymentMethod, $"{v.Description} ({account.Label})", null, _app.Users.Get(v.UserId).FullName,
            v.IsVoided, v.VoidReason, ["المستلم", "المحاسب"]));
    }

    // --- Financial reports ----------------------------------------------------------

    public IDocument TrialBalance(DateOnly? from, DateOnly? to)
    {
        var tb = _app.Reports.TrialBalance(from, to);

        var report = new TableReport(
            "ميزان المراجعة",
            Range(from, to),
            [new("الرمز", 50), new("الحساب"), new("النوع", 70), new("مدين", 90, true), new("دائن", 90, true)],
            tb.Rows.Select(r => new ReportRow([r.Code, r.Name, r.Type.ToArabic(), Blank(r.NetDebit), Blank(r.NetCredit)])).ToList(),
            new ReportRow(["", "الإجمالي", "", tb.TotalDebit.ToDisplay(), tb.TotalCredit.ToDisplay()]),
            Banner: tb.IsBalanced
                ? $"الدفاتر متوازنة: مجموع المدين والدائن {DocumentTheme.Amount(tb.TotalDebit)}."
                : $"الدفاتر غير متوازنة! الفرق {DocumentTheme.Amount(tb.Difference.Abs())}. راجع فحص سلامة البيانات فوراً.",
            BannerIsWarning: !tb.IsBalanced);

        return new TableReportDocument(Shop, report);
    }

    public IDocument GeneralLedger(long accountId, DateOnly? from, DateOnly? to)
    {
        var gl = _app.Reports.GeneralLedger(accountId, from, to);
        var rows = new List<ReportRow> { new(["", "", "الرصيد الافتتاحي", "", "", "", gl.OpeningBalance.ToDisplay()], Bold: true) };

        rows.AddRange(gl.Rows.Select(r => new ReportRow(
            [r.Date.ToString("yyyy-MM-dd"), r.EntryNumber, r.Memo, r.Source.ToArabic(), Blank(r.Debit), Blank(r.Credit), r.Balance.ToDisplay()])));

        return new TableReportDocument(Shop, new TableReport(
            $"دفتر الأستاذ: {gl.Account.Label}",
            $"{Range(from, to)}   |   طبيعة الحساب: {gl.Account.NormalBalance.ToArabic()}",
            [new("التاريخ", 62), new("القيد", 88), new("البيان"), new("المصدر", 70), new("مدين", 70, true), new("دائن", 70, true), new("الرصيد", 80, true)],
            rows,
            new ReportRow(["", "", "الرصيد الختامي", "", gl.TotalDebit.ToDisplay(), gl.TotalCredit.ToDisplay(), gl.ClosingBalance.ToDisplay()]),
            Landscape: true));
    }

    public IDocument FinancialStatements(DateOnly? from, DateOnly? to)
    {
        var income = _app.Reports.IncomeStatement(from, to);
        var sheet = _app.Reports.BalanceSheet(to);

        var incomeReport = new StatementReport(
            "قائمة الدخل",
            Range(from, to),
            [
                new StatementSection("الإيرادات",
                    income.Revenue.Select(r => new StatementLineItem($"{DocumentTheme.Ltr(r.Code)} {r.Name}", r.Amount.ToDisplay())).ToList(),
                    new StatementLineItem("صافي الإيرادات", income.TotalRevenue.ToDisplay())),
                new StatementSection("تكلفة المبيعات",
                    [new StatementLineItem("تكلفة البضاعة المباعة", income.CostOfSales.ToDisplay())],
                    new StatementLineItem("مجمل الربح", income.GrossProfit.ToDisplay())),
                new StatementSection("المصروفات التشغيلية",
                    income.Expenses.Where(e => e.Code != AccountCodes.CostOfGoodsSold)
                        .Select(e => new StatementLineItem($"{DocumentTheme.Ltr(e.Code)} {e.Name}", e.Amount.ToDisplay())).ToList(),
                    new StatementLineItem("إجمالي المصروفات التشغيلية", (income.TotalExpenses - income.CostOfSales).ToDisplay())),
            ],
            [new StatementLineItem(income.NetProfit.IsNegative ? "صافي الخسارة" : "صافي الربح", income.NetProfit.ToDisplay(), Bold: true)]);

        var sheetReport = new StatementReport(
            "قائمة المركز المالي",
            to is null ? "حتى تاريخه" : $"كما في {DocumentTheme.Date(to.Value)}",
            [
                new StatementSection("الأصول",
                    sheet.Assets.Select(r => new StatementLineItem($"{DocumentTheme.Ltr(r.Code)} {r.Name}", r.Amount.ToDisplay())).ToList(),
                    new StatementLineItem("إجمالي الأصول", sheet.TotalAssets.ToDisplay())),
                new StatementSection("الخصوم",
                    sheet.Liabilities.Select(r => new StatementLineItem($"{DocumentTheme.Ltr(r.Code)} {r.Name}", r.Amount.ToDisplay())).ToList(),
                    new StatementLineItem("إجمالي الخصوم", sheet.TotalLiabilities.ToDisplay())),
                new StatementSection("حقوق الملكية",
                    [
                        .. sheet.Equity.Select(r => new StatementLineItem($"{DocumentTheme.Ltr(r.Code)} {r.Name}", r.Amount.ToDisplay())),
                        new StatementLineItem("أرباح الفترة الجارية (غير مقفلة)", sheet.CurrentEarnings.ToDisplay()),
                    ],
                    new StatementLineItem("إجمالي حقوق الملكية", sheet.TotalEquity.ToDisplay())),
            ],
            [new StatementLineItem("إجمالي الخصوم وحقوق الملكية", sheet.TotalLiabilitiesAndEquity.ToDisplay(), Bold: true)],
            sheet.IsBalanced ? null : "الأصول لا تساوي الخصوم وحقوق الملكية! راجع ميزان المراجعة.",
            !sheet.IsBalanced);

        return new StatementReportDocument(Shop, incomeReport, sheetReport);
    }

    public IDocument Vat(DateOnly from, DateOnly to)
    {
        var vat = _app.Reports.Vat(from, to);

        var report = new StatementReport(
            "ملخص ضريبة القيمة المضافة",
            Range(from, to),
            [
                new StatementSection("ضريبة المخرجات (المبيعات)",
                    [
                        new StatementLineItem("المبيعات الخاضعة (بدون الضريبة)", vat.SalesNet.ToDisplay()),
                        new StatementLineItem("ضريبة المبيعات", vat.SalesTax.ToDisplay()),
                        new StatementLineItem("المرتجعات (بدون الضريبة)", vat.ReturnsNet.ToDisplay()),
                        new StatementLineItem("ضريبة المرتجعات", vat.ReturnsTax.ToDisplay()),
                    ],
                    new StatementLineItem("صافي ضريبة المخرجات", vat.OutputTax.ToDisplay())),
                new StatementSection("ضريبة المدخلات (المشتريات)",
                    [
                        new StatementLineItem("المشتريات الخاضعة (بدون الضريبة)", vat.PurchasesNet.ToDisplay()),
                        new StatementLineItem("ضريبة المشتريات القابلة للخصم", vat.PurchasesTax.ToDisplay()),
                    ],
                    null),
            ],
            [
                new StatementLineItem(vat.NetPayable.IsNegative ? "صافي الضريبة المستردة" : "صافي الضريبة المستحقة للسداد", vat.NetPayable.Abs().ToDisplay(), Bold: true),
                new StatementLineItem("حركة حساب الضريبة في الدفاتر (للمطابقة)", vat.LedgerNet.ToDisplay()),
            ],
            vat.AgreesWithLedger ? "مطابق للدفاتر." : "تنبيه: الملخص لا يطابق حركة حساب الضريبة؛ توجد قيود يدوية على الحساب.",
            !vat.AgreesWithLedger);

        return new StatementReportDocument(Shop, report);
    }

    // --- Operational reports --------------------------------------------------------

    public IDocument CashReconciliation(Actor viewer, DateOnly? from, DateOnly? to, long? userId)
    {
        var rows = _app.Reports.CashReconciliation(viewer, from, to, userId);
        var differences = rows.Where(r => r.CashDifference is not null).Sum(r => r.CashDifference!.Value);

        return new TableReportDocument(Shop, new TableReport(
            "مطابقة النقدية للورديات",
            Range(from, to),
            [new("#", 30), new("الموظف"), new("الوردية", 80), new("الفتح", 80), new("الإغلاق", 80),
             new("العهدة", 60, true), new("مبيعات نقدية", 70, true), new("المتوقع", 70, true), new("المعدود", 70, true), new("الفرق", 65, true)],
            rows.Select(r => new ReportRow(
                [r.Id.ToString(), r.EmployeeName, r.ShiftName, r.OpenedAt.ToString("MM-dd HH:mm"), r.ClosedAt?.ToString("MM-dd HH:mm") ?? "مفتوحة",
                 r.OpeningCash.ToDisplay(), DocumentTheme.Amount(r.CashSalesTotal), DocumentTheme.Amount(r.ExpectedCash),
                 DocumentTheme.Amount(r.ActualClosingCash), DocumentTheme.Amount(r.CashDifference)],
                Danger: r.IsShort)).ToList(),
            Summary:
            [
                ("عدد الورديات", rows.Count.ToString()),
                ("صافي الفروقات", differences.ToDisplay()),
                ("ورديات بعجز", rows.Count(r => r.IsShort).ToString()),
                ("ورديات بزيادة", rows.Count(r => r.IsOver).ToString()),
            ],
            Landscape: true,
            Signatures: ["أعدّه", "اعتمده"]));
    }

    public IDocument LowStock()
    {
        var rows = _app.Reports.LowStock();
        var valuation = _app.Reports.StockValuation();

        return new TableReportDocument(Shop, new TableReport(
            "قائمة إعادة الطلب",
            "الأصناف التي بلغت حد إعادة الطلب أو أقل",
            [new("الرمز", 70), new("الصنف"), new("التوافق"), new("الفئة", 70), new("المتوفر", 60), new("حد الطلب", 55), new("النقص", 50), new("التكلفة", 60, true)],
            rows.Select(p => new ReportRow(
                [p.Sku, p.Name, p.FitsModels ?? "", p.CategoryName ?? "", DocumentTheme.Qty(p.StockQty, p.Unit),
                 p.ReorderLevel.ToDisplay(), (p.ReorderLevel - p.StockQty).ToDisplay(), p.CostPrice.ToDisplay()],
                Danger: !p.StockQty.IsPositive)).ToList(),
            Summary:
            [
                ("أصناف نشطة", valuation.ActiveProducts.ToString()),
                ("قيمة المخزون بالتكلفة", valuation.TotalValue.ToDisplay()),
                ("تحتاج إعادة طلب", valuation.LowStockCount.ToString()),
                ("نفدت كميتها", valuation.OutOfStockCount.ToString()),
            ],
            Landscape: true));
    }

    public IDocument StockList()
    {
        var rows = _app.Catalog.Products(activeOnly: true);

        return new TableReportDocument(Shop, new TableReport(
            "جرد المخزون",
            $"كما في {DocumentTheme.DateTime(_app.Clock.Now)}",
            [new("الرمز", 70), new("الصنف"), new("الفئة", 70), new("الكمية", 60), new("متوسط التكلفة", 70, true), new("القيمة", 80, true), new("العدّ الفعلي", 70)],
            rows.Select(p => new ReportRow([p.Sku, p.Name, p.CategoryName ?? "", DocumentTheme.Qty(p.StockQty, p.Unit), p.CostPrice.ToDisplay(), p.StockValue.ToDisplay(), ""])).ToList(),
            new ReportRow(["", "الإجمالي", "", "", "", rows.Sum(p => p.StockValue).ToDisplay(), ""]),
            Landscape: true,
            Signatures: ["القائم بالجرد", "المراجع"]));
    }

    public IDocument SalesByProduct(DateOnly from, DateOnly to)
    {
        var rows = _app.Reports.SalesByProduct(from, to);

        return new TableReportDocument(Shop, new TableReport(
            "المبيعات حسب الصنف",
            Range(from, to) + "   |   القيم بدون الضريبة وبعد المرتجعات",
            [new("الرمز", 70), new("الصنف"), new("المباع", 55), new("المرتجع", 55), new("الصافي", 55), new("القيمة", 75, true), new("التكلفة", 75, true), new("الربح", 75, true), new("الهامش", 50)],
            rows.Select(r => new ReportRow(
                [r.Sku, r.Name, r.QuantitySold.ToDisplay(), r.QuantityReturned.ToDisplay(), r.NetQuantity.ToDisplay(),
                 r.NetValue.ToDisplay(), r.Cost.ToDisplay(), r.Margin.ToDisplay(), r.MarginPercent is { } p ? $"{p}%" : "-"],
                Danger: r.Margin.IsNegative)).ToList(),
            new ReportRow(["", "الإجمالي", "", "", "", rows.Sum(r => r.NetValue).ToDisplay(), rows.Sum(r => r.Cost).ToDisplay(), rows.Sum(r => r.Margin).ToDisplay(), ""]),
            Landscape: true));
    }

    public IDocument SalesByEmployee(DateOnly from, DateOnly to)
    {
        var rows = _app.Reports.SalesByEmployee(from, to);

        return new TableReportDocument(Shop, new TableReport(
            "المبيعات حسب الموظف",
            Range(from, to),
            [new("الموظف"), new("الفواتير", 55), new("المبيعات", 85, true), new("المرتجعات", 85, true), new("مجمل الربح", 85, true), new("فروقات الصندوق", 85, true)],
            rows.Select(r => new ReportRow(
                [r.EmployeeName, r.InvoiceCount.ToString(), r.SalesTotal.ToDisplay(), r.RefundsTotal.ToDisplay(), r.GrossMargin.ToDisplay(), r.CashDifferences.ToDisplay()],
                Danger: r.CashDifferences.IsNegative)).ToList()));
    }

    public IDocument CustomerStatement(long customerId, DateOnly? from, DateOnly? to)
    {
        var customer = _app.Parties.GetCustomer(customerId);
        return Statement($"كشف حساب العميل: {customer.Name}", _app.Parties.CustomerStatement(customerId, from, to), from, to, "مدين (عليه)", "دائن (له)");
    }

    public IDocument SupplierStatement(long supplierId, DateOnly? from, DateOnly? to)
    {
        var supplier = _app.Parties.GetSupplier(supplierId);
        return Statement($"كشف حساب المورد: {supplier.Name}", _app.Parties.SupplierStatement(supplierId, from, to), from, to, "مدين (مدفوع)", "دائن (مستحق)");
    }

    public IDocument PayrollSheet(long runId)
    {
        var run = _app.Payroll.Get(runId);
        var lines = _app.Payroll.Lines(runId);

        return new TableReportDocument(Shop, new TableReport(
            $"مسير رواتب شهر {PayrollService.MonthName(run.PeriodMonth)} {DocumentTheme.Ltr(run.PeriodYear.ToString())}",
            $"{(run.RunNumber is null ? "مسودة" : DocumentTheme.Ltr(run.RunNumber))}   |   الحالة: {run.Status.ToArabic()}",
            [new("الموظف"), new("الراتب الأساسي", 85, true), new("الإضافات", 75, true), new("الخصومات", 75, true), new("الصافي", 85, true), new("ملاحظات"), new("التوقيع", 80)],
            lines.Select(l => new ReportRow([l.EmployeeName, l.BaseSalary.ToDisplay(), l.Additions.ToDisplay(), l.Deductions.ToDisplay(), l.NetPay.ToDisplay(), l.Notes ?? "", ""])).ToList(),
            new ReportRow(["الإجمالي", run.TotalBase.ToDisplay(), run.TotalAdditions.ToDisplay(), run.TotalDeductions.ToDisplay(), run.TotalNet.ToDisplay(), "", ""]),
            Landscape: true,
            Signatures: ["أعدّه", "المالك"]));
    }

    public IDocument IntegrityReport()
    {
        var checks = _app.Reports.IntegrityChecks();
        var allPassed = checks.All(c => c.Passed);

        return new TableReportDocument(Shop, new TableReport(
            "فحص سلامة البيانات",
            $"أُجري في {DocumentTheme.DateTime(_app.Clock.Now)}",
            [new("الفحص"), new("النتيجة", 60), new("القيمة الفعلية", 110), new("القيمة المتوقعة", 110)],
            checks.Select(c => new ReportRow([c.Name, c.Passed ? "سليم" : "خلل", c.Actual, c.Expected], Danger: !c.Passed)).ToList(),
            Banner: allPassed ? "جميع الفحوصات سليمة: الدفاتر متطابقة مع المخزون والعملاء والموردين." : "يوجد خلل في فحص واحد أو أكثر. تواصل مع الدعم الفني ولا تُقفل الفترة.",
            BannerIsWarning: !allPassed));
    }

    private IDocument Statement(string title, IReadOnlyList<StatementLine> lines, DateOnly? from, DateOnly? to, string debitTitle, string creditTitle) =>
        new TableReportDocument(Shop, new TableReport(
            title,
            Range(from, to),
            [new("التاريخ", 65), new("المستند", 105), new("البيان"), new(debitTitle, 80, true), new(creditTitle, 80, true), new("الرصيد", 85, true)],
            lines.Select(l => new ReportRow(
                [l.Reference is null ? "" : l.Date.ToString("yyyy-MM-dd"), l.Reference ?? "", l.Description, Blank(l.Debit), Blank(l.Credit), l.Balance.ToDisplay()],
                Bold: l.Reference is null)).ToList(),
            Summary: [("الرصيد الختامي", lines.Count > 0 ? lines[^1].Balance.ToDisplay() : "0.00")]));

    private static string Blank(Money value) => value.IsZero ? "" : value.ToDisplay();

    private static string Range(DateOnly? from, DateOnly? to) =>
        (from, to) switch
        {
            (null, null) => "كل الفترات",
            ({ } f, null) => $"من {DocumentTheme.Date(f)}",
            (null, { } t) => $"حتى {DocumentTheme.Date(t)}",
            ({ } f, { } t) when f == t => $"يوم {DocumentTheme.Date(f)}",
            ({ } f, { } t) => $"من {DocumentTheme.Date(f)} إلى {DocumentTheme.Date(t)}",
        };
}
