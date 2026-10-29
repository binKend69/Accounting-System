using MotoParts.Core.Common;
using MotoParts.Core.Data;
using MotoParts.Core.Domain;
using MotoParts.Core.Security;

namespace MotoParts.Core.Services;

/// <summary>
/// Read-only reports.
///
/// Financial statements come from posted journal lines only - never from the
/// totals cached on documents. If a report and the ledger ever disagreed the
/// ledger would be right, and reading it directly means they cannot.
/// Operational reports (stock, tills, sales by product) read the documents,
/// and the integrity check proves the two views agree.
/// </summary>
public sealed class ReportService
{
    private readonly DbSession _db;
    private readonly IClock _clock;
    private readonly InventoryService _inventory;
    private readonly ShiftService _shifts;

    public ReportService(DbSession db, IClock clock, InventoryService inventory, ShiftService shifts)
    {
        _db = db;
        _clock = clock;
        _inventory = inventory;
        _shifts = shifts;
    }

    // --- Ledger reports ---------------------------------------------------------

    /// <summary>
    /// Every account with posted activity, each shown once on its net side.
    /// The totals must be equal: that equality is the system health check.
    /// </summary>
    public TrialBalance TrialBalance(DateOnly? from = null, DateOnly? to = null)
    {
        var rows = AccountTotals(from, to)
            .Select(t =>
            {
                var net = t.Debit - t.Credit;
                return new TrialBalanceRow(t.AccountId, t.Code, t.Name, t.Type, t.Debit, t.Credit,
                    net.IsPositive ? net : Money.Zero, net.IsNegative ? -net : Money.Zero);
            })
            .ToList();

        return new TrialBalance(rows, rows.Sum(r => r.NetDebit), rows.Sum(r => r.NetCredit), from, to);
    }

    /// <summary>One account: opening balance, each movement with a running balance, closing balance.</summary>
    public GeneralLedger GeneralLedger(long accountId, DateOnly? from = null, DateOnly? to = null)
    {
        var account = _db.QuerySingle<Account>("SELECT * FROM accounts WHERE id = @accountId", new { accountId });
        var debitNormal = account.NormalBalance == NormalBalance.Debit;

        var opening = Money.Zero;

        if (from is not null)
        {
            var before = _db.QuerySingle<(long Debit, long Credit)>(
                """
                SELECT COALESCE(SUM(l.debit), 0), COALESCE(SUM(l.credit), 0)
                FROM journal_lines l JOIN journal_entries e ON e.id = l.journal_entry_id
                WHERE l.account_id = @accountId AND e.posted_at IS NOT NULL AND e.entry_date < @from
                """,
                new { accountId, from });

            opening = Directional(Money.FromMinorUnits(before.Debit), Money.FromMinorUnits(before.Credit), debitNormal);
        }

        var lines = _db.Query<LedgerRowData>(
            """
            SELECT e.id AS entry_id, e.entry_date, e.entry_number, COALESCE(l.memo, e.memo) AS memo, e.memo AS entry_memo,
                   e.source, l.debit, l.credit
            FROM journal_lines l JOIN journal_entries e ON e.id = l.journal_entry_id
            WHERE l.account_id = @accountId AND e.posted_at IS NOT NULL
              AND (@from IS NULL OR e.entry_date >= @from)
              AND (@to IS NULL OR e.entry_date <= @to)
            ORDER BY e.entry_date, e.id, l.line_number
            """,
            new { accountId, from, to }).ToList();

        var running = opening;
        var rows = new List<GeneralLedgerRow>();

        foreach (var line in lines)
        {
            running += Directional(line.Debit, line.Credit, debitNormal);
            rows.Add(new GeneralLedgerRow(line.EntryId, line.EntryDate, line.EntryNumber,
                line.Memo == line.EntryMemo ? line.Memo : $"{line.EntryMemo} - {line.Memo}",
                line.Source, line.Debit, line.Credit, running));
        }

        return new GeneralLedger(account, opening, rows, running, rows.Sum(r => r.Debit), rows.Sum(r => r.Credit), from, to);
    }

    /// <summary>Revenue (net of returns) less expenses, for a date range.</summary>
    public IncomeStatement IncomeStatement(DateOnly? from = null, DateOnly? to = null)
    {
        var revenue = new List<StatementRow>();
        var expenses = new List<StatementRow>();
        var costOfSales = Money.Zero;

        foreach (var t in AccountTotals(from, to).Where(t => !t.Type.IsBalanceSheet()))
        {
            if (t.Type == AccountType.Revenue)
            {
                // Credit-normal; a debit balance is contra-revenue (returns).
                revenue.Add(new StatementRow(t.AccountId, t.Code, t.Name, t.Credit - t.Debit));
                continue;
            }

            var amount = t.Debit - t.Credit;
            expenses.Add(new StatementRow(t.AccountId, t.Code, t.Name, amount));

            if (t.Code == AccountCodes.CostOfGoodsSold)
            {
                costOfSales += amount;
            }
        }

        return new IncomeStatement(revenue, expenses, revenue.Sum(r => r.Amount), expenses.Sum(r => r.Amount), costOfSales, from, to);
    }

    /// <summary>
    /// Assets = liabilities + equity as at a date, with the period's profit
    /// shown as current earnings until it is closed out to retained earnings.
    /// </summary>
    public BalanceSheet BalanceSheet(DateOnly? asOf = null)
    {
        var assets = new List<StatementRow>();
        var liabilities = new List<StatementRow>();
        var equity = new List<StatementRow>();
        var earnings = Money.Zero;

        foreach (var t in AccountTotals(null, asOf))
        {
            switch (t.Type)
            {
                case AccountType.Asset:
                    assets.Add(new StatementRow(t.AccountId, t.Code, t.Name, t.Debit - t.Credit));
                    break;
                case AccountType.Liability:
                    liabilities.Add(new StatementRow(t.AccountId, t.Code, t.Name, t.Credit - t.Debit));
                    break;
                case AccountType.Equity:
                    equity.Add(new StatementRow(t.AccountId, t.Code, t.Name, t.Credit - t.Debit));
                    break;
                case AccountType.Revenue:
                    earnings += t.Credit - t.Debit;
                    break;
                case AccountType.Expense:
                    earnings -= t.Debit - t.Credit;
                    break;
            }
        }

        return new BalanceSheet(assets, liabilities, equity, earnings, asOf);
    }

    // --- Tax ----------------------------------------------------------------------

    /// <summary>
    /// VAT for a return period: output tax on sales less refunds, input tax on
    /// received purchases, and the net payable - from the documents, with the
    /// ledger's Tax Payable movement alongside as a cross-check.
    /// </summary>
    public VatSummary Vat(DateOnly from, DateOnly to)
    {
        long Sum(string sql) => _db.ExecuteScalar<long>(sql, new { from, to });

        var salesTax = Money.FromMinorUnits(Sum("SELECT COALESCE(SUM(tax_total), 0) FROM sales WHERE sale_date BETWEEN @from AND @to"));
        var salesNet = Money.FromMinorUnits(Sum("SELECT COALESCE(SUM(total - tax_total), 0) FROM sales WHERE sale_date BETWEEN @from AND @to"));
        var refundTax = Money.FromMinorUnits(Sum("SELECT COALESCE(SUM(tax_total), 0) FROM sale_refunds WHERE refund_date BETWEEN @from AND @to"));
        var refundNet = Money.FromMinorUnits(Sum("SELECT COALESCE(SUM(total - tax_total), 0) FROM sale_refunds WHERE refund_date BETWEEN @from AND @to"));
        var purchaseTax = Money.FromMinorUnits(Sum("SELECT COALESCE(SUM(tax_total), 0) FROM purchases WHERE status = 'Received' AND purchase_date BETWEEN @from AND @to"));
        var purchaseNet = Money.FromMinorUnits(Sum("SELECT COALESCE(SUM(subtotal), 0) FROM purchases WHERE status = 'Received' AND purchase_date BETWEEN @from AND @to"));

        var ledger = _db.QuerySingle<(long Debit, long Credit)>(
            """
            SELECT COALESCE(SUM(l.debit), 0), COALESCE(SUM(l.credit), 0)
            FROM journal_lines l
            JOIN journal_entries e ON e.id = l.journal_entry_id
            JOIN accounts a ON a.id = l.account_id
            WHERE a.code = @code AND e.posted_at IS NOT NULL AND e.entry_date BETWEEN @from AND @to
            """,
            new { code = AccountCodes.TaxPayable, from, to });

        return new VatSummary(from, to, salesNet, salesTax, refundNet, refundTax, purchaseNet, purchaseTax,
            Money.FromMinorUnits(ledger.Credit - ledger.Debit));
    }

    // --- Tills ----------------------------------------------------------------------

    /// <summary>Everything needed for one till's closing report.</summary>
    public ShiftReconciliation ShiftReconciliation(long sessionId)
    {
        var session = _shifts.Sessions(null, null).FirstOrDefault(s => s.Id == sessionId)
            ?? throw new BusinessRuleException("الوردية غير موجودة.");

        var totals = _shifts.DrawerTotals(sessionId);

        var byMethod = _db.Query<MethodRow>(
            """
            SELECT payment_method AS method, COUNT(*) AS count, COALESCE(SUM(total), 0) AS sales, 0 AS refunds
            FROM sales WHERE shift_session_id = @sessionId GROUP BY payment_method
            UNION ALL
            SELECT payment_method, 0, 0, COALESCE(SUM(total), 0)
            FROM sale_refunds WHERE shift_session_id = @sessionId GROUP BY payment_method
            """,
            new { sessionId })
            .GroupBy(r => r.Method)
            .Select(g => new PaymentMethodTotal(g.Key, g.Sum(r => r.Count), g.Sum(r => r.Sales), g.Sum(r => r.Refunds)))
            .OrderBy(m => m.Method)
            .ToList();

        var sales = _db.Query<SessionSale>(
            """
            SELECT s.invoice_number, s.sold_at, s.payment_method, s.total, s.cogs_total, s.status, c.name AS customer_name
            FROM sales s LEFT JOIN customers c ON c.id = s.customer_id
            WHERE s.shift_session_id = @sessionId ORDER BY s.id
            """,
            new { sessionId }).ToList();

        var refunds = _db.Query<SessionRefund>(
            """
            SELECT r.refund_number, s.invoice_number, r.refunded_at, r.payment_method, r.total, r.reason
            FROM sale_refunds r JOIN sales s ON s.id = r.sale_id
            WHERE r.shift_session_id = @sessionId ORDER BY r.id
            """,
            new { sessionId }).ToList();

        var drawerVouchers = _db.Query<SessionVoucher>(
            """
            SELECT receipt_number AS number, 'سند قبض' AS kind, amount, 1 AS is_inflow, voided_at IS NOT NULL AS is_voided
            FROM customer_receipts WHERE shift_session_id = @sessionId
            UNION ALL
            SELECT payment_number, 'سند صرف لمورد', amount, 0, voided_at IS NOT NULL
            FROM supplier_payments WHERE shift_session_id = @sessionId
            UNION ALL
            SELECT voucher_number, 'سند مصروف', amount, 0, voided_at IS NOT NULL
            FROM expense_vouchers WHERE shift_session_id = @sessionId
            """,
            new { sessionId }).ToList();

        return new ShiftReconciliation(session, totals, byMethod, sales, refunds, drawerVouchers);
    }

    /// <summary>Cash reconciliation across sessions. Viewers without view-all see only their own.</summary>
    public IReadOnlyList<ShiftSessionRow> CashReconciliation(Actor viewer, DateOnly? from, DateOnly? to, long? userId = null)
    {
        var effectiveUser = viewer.Can(Permission.ReportViewAll) || viewer.Can(Permission.ShiftViewAll) ? userId : viewer.Id;
        var rows = _shifts.Sessions(from, to, effectiveUser);

        // Open sessions are reconciled against live figures.
        foreach (var row in rows.Where(r => r.Status == ShiftSessionStatus.Open))
        {
            var live = _shifts.DrawerTotals(row.Id);
            row.CashSalesTotal = live.CashSales;
            row.CashRefundsTotal = live.CashRefunds;
            row.CashReceiptsTotal = live.CashReceipts;
            row.CashPayoutsTotal = live.CashPayouts;
            row.ExpectedCash = live.Expected;
        }

        return rows;
    }

    // --- Stock ----------------------------------------------------------------------

    /// <summary>Parts at or below their reorder level, most urgent first.</summary>
    public IReadOnlyList<ProductRow> LowStock() =>
        _db.Query<ProductRow>(
            """
            SELECT p.*, c.name AS category_name
            FROM products p LEFT JOIN categories c ON c.id = p.category_id
            WHERE p.is_active = 1 AND p.reorder_level > 0 AND p.stock_qty <= p.reorder_level
            ORDER BY (p.stock_qty - p.reorder_level), p.name
            """).ToList();

    public StockValuation StockValuation()
    {
        var row = _db.QuerySingle<(long Products, long Value, long OutOfStock, long Low)>(
            """
            SELECT COUNT(*),
                   COALESCE(SUM(stock_value), 0),
                   COALESCE(SUM(CASE WHEN stock_qty <= 0 THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN reorder_level > 0 AND stock_qty <= reorder_level THEN 1 ELSE 0 END), 0)
            FROM products WHERE is_active = 1
            """);

        // Inactive products can still hold stock; the books include them.
        var allValue = _db.ExecuteScalar<long>("SELECT COALESCE(SUM(stock_value), 0) FROM products");

        return new StockValuation((int)row.Products, Money.FromMinorUnits(allValue), (int)row.OutOfStock, (int)row.Low);
    }

    // --- Sales ----------------------------------------------------------------------

    /// <summary>
    /// Quantity, value (excluding tax) and margin per product for a range.
    /// Returns are counted on the day they came back.
    /// </summary>
    public IReadOnlyList<ProductSalesRow> SalesByProduct(DateOnly from, DateOnly to)
    {
        var sold = _db.Query<ProductLineData>(
            """
            SELECT i.product_id, i.sku, i.name, i.quantity, i.line_total, i.line_cogs, s.prices_include_tax, s.tax_rate_bp,
                   s.subtotal - s.discount_total AS sale_net, s.tax_total AS sale_tax
            FROM sale_items i JOIN sales s ON s.id = i.sale_id
            WHERE s.sale_date BETWEEN @from AND @to
            """,
            new { from, to });

        var returned = _db.Query<ProductLineData>(
            """
            SELECT ri.product_id, i.sku, i.name, ri.quantity, ri.line_total, ri.line_cogs, s.prices_include_tax, s.tax_rate_bp,
                   s.subtotal - s.discount_total AS sale_net, s.tax_total AS sale_tax
            FROM sale_refund_items ri
            JOIN sale_refunds r ON r.id = ri.sale_refund_id
            JOIN sale_items i ON i.id = ri.sale_item_id
            JOIN sales s ON s.id = r.sale_id
            WHERE r.refund_date BETWEEN @from AND @to
            """,
            new { from, to });

        var rows = new Dictionary<long, ProductSalesRow>();

        void Add(ProductLineData line, int sign)
        {
            var row = rows.TryGetValue(line.ProductId, out var existing)
                ? existing
                : rows[line.ProductId] = new ProductSalesRow { ProductId = line.ProductId, Sku = line.Sku, Name = line.Name };

            var value = line.PricesIncludeTax ? ExcludingTax(line.LineTotal, line.TaxRateBp) : line.LineTotal;

            if (sign > 0)
            {
                row.QuantitySold += line.Quantity;
            }
            else
            {
                row.QuantityReturned += line.Quantity;
            }

            row.NetValue += sign > 0 ? value : -value;
            row.Cost += sign > 0 ? line.LineCogs : -line.LineCogs;
        }

        foreach (var line in sold)
        {
            Add(line, +1);
        }

        foreach (var line in returned)
        {
            Add(line, -1);
        }

        return rows.Values.OrderByDescending(r => r.NetValue).ToList();
    }

    /// <summary>Invoice count, takings and returns per employee for a range.</summary>
    public IReadOnlyList<EmployeeSalesRow> SalesByEmployee(DateOnly from, DateOnly to) =>
        _db.Query<EmployeeSalesRow>(
            """
            SELECT u.id AS user_id, u.full_name AS employee_name,
                   (SELECT COUNT(*) FROM sales s WHERE s.user_id = u.id AND s.sale_date BETWEEN @from AND @to) AS invoice_count,
                   (SELECT COALESCE(SUM(total), 0) FROM sales s WHERE s.user_id = u.id AND s.sale_date BETWEEN @from AND @to) AS sales_total,
                   (SELECT COALESCE(SUM(total), 0) FROM sale_refunds r WHERE r.user_id = u.id AND r.refund_date BETWEEN @from AND @to) AS refunds_total,
                   (SELECT COALESCE(SUM(s.total - s.tax_total - s.cogs_total), 0) FROM sales s WHERE s.user_id = u.id AND s.sale_date BETWEEN @from AND @to) AS gross_margin,
                   (SELECT COALESCE(SUM(cash_difference), 0) FROM shift_sessions ss WHERE ss.user_id = u.id AND date(ss.opened_at) BETWEEN @from AND @to) AS cash_differences
            FROM users u
            WHERE EXISTS (SELECT 1 FROM sales s WHERE s.user_id = u.id AND s.sale_date BETWEEN @from AND @to)
               OR EXISTS (SELECT 1 FROM shift_sessions ss WHERE ss.user_id = u.id AND date(ss.opened_at) BETWEEN @from AND @to)
            ORDER BY sales_total DESC
            """,
            new { from, to }).ToList();

    /// <summary>A day's takings, for the summary at the top of the sales report.</summary>
    public SalesSummary SalesSummary(DateOnly from, DateOnly to, long? userId = null)
    {
        var row = _db.QuerySingle<(long Count, long Total, long Tax, long Cogs, long Discounts)>(
            """
            SELECT COUNT(*), COALESCE(SUM(total), 0), COALESCE(SUM(tax_total), 0), COALESCE(SUM(cogs_total), 0), COALESCE(SUM(discount_total), 0)
            FROM sales WHERE sale_date BETWEEN @from AND @to AND (@userId IS NULL OR user_id = @userId)
            """,
            new { from, to, userId });

        var refunds = _db.QuerySingle<(long Total, long Tax, long Cogs)>(
            """
            SELECT COALESCE(SUM(total), 0), COALESCE(SUM(tax_total), 0), COALESCE(SUM(restocked_cogs), 0)
            FROM sale_refunds WHERE refund_date BETWEEN @from AND @to AND (@userId IS NULL OR user_id = @userId)
            """,
            new { from, to, userId });

        return new SalesSummary(
            (int)row.Count,
            Money.FromMinorUnits(row.Total),
            Money.FromMinorUnits(row.Tax),
            Money.FromMinorUnits(row.Cogs),
            Money.FromMinorUnits(row.Discounts),
            Money.FromMinorUnits(refunds.Total),
            Money.FromMinorUnits(refunds.Tax),
            Money.FromMinorUnits(refunds.Cogs));
    }

    // --- Dashboard ----------------------------------------------------------------

    public Dashboard Dashboard(Actor viewer)
    {
        var today = _clock.Today;
        var seesAll = viewer.Can(Permission.ReportViewAll);
        var summary = SalesSummary(today, today, seesAll ? null : viewer.Id);
        var session = _shifts.OpenSessionFor(viewer.Id);
        var valuation = StockValuation();

        // Dates come from the application clock, never SQLite's 'now', so the
        // dashboard and every other figure agree on what "today" is.
        var since = today.AddDays(-30);

        long Count(string sql) => _db.ExecuteScalar<long>(sql, new { since });

        return new Dashboard(
            summary,
            session,
            session is null ? null : _shifts.DrawerTotals(session.Id),
            valuation.LowStockCount,
            valuation.OutOfStockCount,
            (int)Count("SELECT COUNT(*) FROM shift_sessions WHERE cash_difference IS NOT NULL AND cash_difference <> 0 AND date(closed_at) >= @since"),
            (int)Count("SELECT COUNT(*) FROM shift_sessions WHERE status = 'Open'"),
            seesAll ? Money.FromMinorUnits(Count($"SELECT COALESCE(SUM({PartyService.CustomerBalanceSql("c.id")}), 0) FROM customers c")) : null,
            seesAll ? Money.FromMinorUnits(Count($"SELECT COALESCE(SUM({PartyService.SupplierBalanceSql("s.id")}), 0) FROM suppliers s")) : null);
    }

    // --- Integrity ------------------------------------------------------------------

    /// <summary>
    /// Proves the books are internally consistent: each control account equals
    /// the sub-ledger it summarises, the ledger balances, the stock cache
    /// matches its history, and the database file itself is sound.
    /// </summary>
    public IReadOnlyList<IntegrityCheck> IntegrityChecks()
    {
        var checks = new List<IntegrityCheck>();
        var trial = TrialBalance();

        checks.Add(new IntegrityCheck("توازن الدفاتر (مجموع المدين = مجموع الدائن)",
            trial.IsBalanced, trial.TotalDebit.ToDisplay(), trial.TotalCredit.ToDisplay()));

        Money Ledger(string code) => Money.FromMinorUnits(_db.ExecuteScalar<long>(
            """
            SELECT COALESCE(SUM(l.debit) - SUM(l.credit), 0)
            FROM journal_lines l JOIN journal_entries e ON e.id = l.journal_entry_id JOIN accounts a ON a.id = l.account_id
            WHERE a.code = @code AND e.posted_at IS NOT NULL
            """,
            new { code }));

        var inventoryLedger = Ledger(AccountCodes.Inventory);
        var shelf = Money.FromMinorUnits(_db.ExecuteScalar<long>("SELECT COALESCE(SUM(stock_value), 0) FROM products"));
        checks.Add(new IntegrityCheck("حساب المخزون = قيمة البضاعة على الرفوف",
            inventoryLedger == shelf, inventoryLedger.ToDisplay(), shelf.ToDisplay()));

        var receivableLedger = Ledger(AccountCodes.AccountsReceivable);
        var customers = Money.FromMinorUnits(_db.ExecuteScalar<long>($"SELECT COALESCE(SUM({PartyService.CustomerBalanceSql("c.id")}), 0) FROM customers c"));
        checks.Add(new IntegrityCheck("حساب الذمم المدينة = مجموع أرصدة العملاء",
            receivableLedger == customers, receivableLedger.ToDisplay(), customers.ToDisplay()));

        var payableLedger = -Ledger(AccountCodes.AccountsPayable);
        var suppliers = Money.FromMinorUnits(_db.ExecuteScalar<long>($"SELECT COALESCE(SUM({PartyService.SupplierBalanceSql("s.id")}), 0) FROM suppliers s"));
        checks.Add(new IntegrityCheck("حساب الذمم الدائنة = مجموع أرصدة الموردين",
            payableLedger == suppliers, payableLedger.ToDisplay(), suppliers.ToDisplay()));

        var salariesLedger = -Ledger(AccountCodes.SalariesPayable);
        var unpaid = Money.FromMinorUnits(_db.ExecuteScalar<long>("SELECT COALESCE(SUM(total_net), 0) FROM payroll_runs WHERE status = 'Posted'"));
        checks.Add(new IntegrityCheck("حساب الرواتب المستحقة = المسيرات المعتمدة غير المصروفة",
            salariesLedger == unpaid, salariesLedger.ToDisplay(), unpaid.ToDisplay()));

        var drift = _inventory.FindDrift().Count;
        checks.Add(new IntegrityCheck("أرصدة الأصناف مطابقة لسجل حركات المخزون",
            drift == 0, drift == 0 ? "مطابق" : $"{drift} صنف غير مطابق", "مطابق"));

        var unposted = _db.ExecuteScalar<long>("SELECT COUNT(*) FROM journal_entries WHERE posted_at IS NULL");
        checks.Add(new IntegrityCheck("لا توجد قيود مسودة عالقة", unposted == 0, unposted.ToString(), "0"));

        var integrity = _db.ExecuteScalar<string>("PRAGMA integrity_check") ?? "";
        checks.Add(new IntegrityCheck("سلامة ملف قاعدة البيانات", integrity == "ok", integrity, "ok"));

        var orphans = _db.Query<object>("PRAGMA foreign_key_check").Count();
        checks.Add(new IntegrityCheck("سلامة الروابط بين الجداول", orphans == 0, orphans.ToString(), "0"));

        return checks;
    }

    // --- Internals ------------------------------------------------------------------

    private IReadOnlyList<AccountTotal> AccountTotals(DateOnly? from, DateOnly? to) =>
        _db.Query<AccountTotal>(
            """
            SELECT a.id AS account_id, a.code, a.name, a.type,
                   COALESCE(SUM(l.debit), 0) AS debit, COALESCE(SUM(l.credit), 0) AS credit
            FROM journal_lines l
            JOIN journal_entries e ON e.id = l.journal_entry_id
            JOIN accounts a ON a.id = l.account_id
            WHERE e.posted_at IS NOT NULL
              AND (@from IS NULL OR e.entry_date >= @from)
              AND (@to IS NULL OR e.entry_date <= @to)
            GROUP BY a.id, a.code, a.name, a.type
            ORDER BY a.code
            """,
            new { from, to }).ToList();

    private static Money Directional(Money debit, Money credit, bool debitNormal) =>
        debitNormal ? debit - credit : credit - debit;

    private static Money ExcludingTax(Money inclusive, int taxRateBp) =>
        Money.Of(inclusive.Amount * 10000m / (10000m + taxRateBp));

    private sealed class AccountTotal
    {
        public long AccountId { get; set; }
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public AccountType Type { get; set; }
        public Money Debit { get; set; }
        public Money Credit { get; set; }
    }

    private sealed class LedgerRowData
    {
        public long EntryId { get; set; }
        public DateOnly EntryDate { get; set; }
        public string EntryNumber { get; set; } = "";
        public string Memo { get; set; } = "";
        public string EntryMemo { get; set; } = "";
        public JournalSource Source { get; set; }
        public Money Debit { get; set; }
        public Money Credit { get; set; }
    }

    private sealed class MethodRow
    {
        public PaymentMethod Method { get; set; }
        public int Count { get; set; }
        public Money Sales { get; set; }
        public Money Refunds { get; set; }
    }

    private sealed class ProductLineData
    {
        public long ProductId { get; set; }
        public string Sku { get; set; } = "";
        public string Name { get; set; } = "";
        public Quantity Quantity { get; set; }
        public Money LineTotal { get; set; }
        public Money LineCogs { get; set; }
        public bool PricesIncludeTax { get; set; }
        public int TaxRateBp { get; set; }
    }
}

public sealed record TrialBalanceRow(long AccountId, string Code, string Name, AccountType Type,
    Money DebitTotal, Money CreditTotal, Money NetDebit, Money NetCredit);

public sealed record TrialBalance(IReadOnlyList<TrialBalanceRow> Rows, Money TotalDebit, Money TotalCredit, DateOnly? From, DateOnly? To)
{
    public Money Difference => TotalDebit - TotalCredit;

    public bool IsBalanced => Difference.IsZero;
}

public sealed record GeneralLedgerRow(long EntryId, DateOnly Date, string EntryNumber, string Memo, JournalSource Source,
    Money Debit, Money Credit, Money Balance);

public sealed record GeneralLedger(Account Account, Money OpeningBalance, IReadOnlyList<GeneralLedgerRow> Rows,
    Money ClosingBalance, Money TotalDebit, Money TotalCredit, DateOnly? From, DateOnly? To);

public sealed record StatementRow(long AccountId, string Code, string Name, Money Amount);

public sealed record IncomeStatement(IReadOnlyList<StatementRow> Revenue, IReadOnlyList<StatementRow> Expenses,
    Money TotalRevenue, Money TotalExpenses, Money CostOfSales, DateOnly? From, DateOnly? To)
{
    public Money GrossProfit => TotalRevenue - CostOfSales;

    public Money NetProfit => TotalRevenue - TotalExpenses;
}

public sealed record BalanceSheet(IReadOnlyList<StatementRow> Assets, IReadOnlyList<StatementRow> Liabilities,
    IReadOnlyList<StatementRow> Equity, Money CurrentEarnings, DateOnly? AsOf)
{
    public Money TotalAssets => Assets.Sum(r => r.Amount);

    public Money TotalLiabilities => Liabilities.Sum(r => r.Amount);

    public Money TotalEquity => Equity.Sum(r => r.Amount) + CurrentEarnings;

    public Money TotalLiabilitiesAndEquity => TotalLiabilities + TotalEquity;

    public bool IsBalanced => TotalAssets == TotalLiabilitiesAndEquity;
}

public sealed record VatSummary(DateOnly From, DateOnly To,
    Money SalesNet, Money SalesTax, Money ReturnsNet, Money ReturnsTax, Money PurchasesNet, Money PurchasesTax, Money LedgerNet)
{
    public Money OutputTax => SalesTax - ReturnsTax;

    public Money NetPayable => OutputTax - PurchasesTax;

    /// <summary>The documents and the ledger tell the same story.</summary>
    public bool AgreesWithLedger => NetPayable == LedgerNet;
}

public sealed record PaymentMethodTotal(PaymentMethod Method, int SalesCount, Money Sales, Money Refunds)
{
    public Money Net => Sales - Refunds;
}

public sealed class SessionSale
{
    public string InvoiceNumber { get; set; } = "";
    public DateTime SoldAt { get; set; }
    public PaymentMethod PaymentMethod { get; set; }
    public Money Total { get; set; }
    public Money CogsTotal { get; set; }
    public SaleStatus Status { get; set; }
    public string? CustomerName { get; set; }
}

public sealed class SessionRefund
{
    public string RefundNumber { get; set; } = "";
    public string InvoiceNumber { get; set; } = "";
    public DateTime RefundedAt { get; set; }
    public PaymentMethod PaymentMethod { get; set; }
    public Money Total { get; set; }
    public string Reason { get; set; } = "";
}

public sealed class SessionVoucher
{
    public string Number { get; set; } = "";
    public string Kind { get; set; } = "";
    public Money Amount { get; set; }
    public bool IsInflow { get; set; }
    public bool IsVoided { get; set; }
}

public sealed record ShiftReconciliation(
    ShiftSessionRow Session,
    DrawerTotals Totals,
    IReadOnlyList<PaymentMethodTotal> ByPaymentMethod,
    IReadOnlyList<SessionSale> Sales,
    IReadOnlyList<SessionRefund> Refunds,
    IReadOnlyList<SessionVoucher> DrawerVouchers);

public sealed record StockValuation(int ActiveProducts, Money TotalValue, int OutOfStockCount, int LowStockCount);

public sealed class ProductSalesRow
{
    public long ProductId { get; set; }
    public string Sku { get; set; } = "";
    public string Name { get; set; } = "";
    public Quantity QuantitySold { get; set; }
    public Quantity QuantityReturned { get; set; }
    public Money NetValue { get; set; }
    public Money Cost { get; set; }

    public Quantity NetQuantity => QuantitySold - QuantityReturned;

    public Money Margin => NetValue - Cost;

    public decimal? MarginPercent => NetValue.IsZero ? null : Math.Round(Margin.Amount / NetValue.Amount * 100m, 1);
}

public sealed class EmployeeSalesRow
{
    public long UserId { get; set; }
    public string EmployeeName { get; set; } = "";
    public long InvoiceCount { get; set; }
    public Money SalesTotal { get; set; }
    public Money RefundsTotal { get; set; }
    public Money GrossMargin { get; set; }
    public Money CashDifferences { get; set; }
}

public sealed record SalesSummary(int InvoiceCount, Money GrossTakings, Money TaxCollected, Money CostOfSales, Money Discounts,
    Money Refunded, Money RefundedTax, Money RestockedCost)
{
    public Money NetTakings => GrossTakings - Refunded;

    /// <summary>Revenue excluding tax, net of returns.</summary>
    public Money NetRevenue => GrossTakings - TaxCollected - (Refunded - RefundedTax);

    public Money GrossProfit => NetRevenue - (CostOfSales - RestockedCost);
}

public sealed record Dashboard(
    SalesSummary Today,
    ShiftSession? MyOpenSession,
    DrawerTotals? MyDrawer,
    int LowStockCount,
    int OutOfStockCount,
    int RecentCashDifferences,
    int OpenTills,
    Money? Receivables,
    Money? Payables);

public sealed record IntegrityCheck(string Name, bool Passed, string Actual, string Expected);
