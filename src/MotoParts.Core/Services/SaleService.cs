using MotoParts.Core.Common;
using MotoParts.Core.Data;
using MotoParts.Core.Domain;
using MotoParts.Core.Security;

namespace MotoParts.Core.Services;

/// <summary>
/// Checkout and returns.
///
/// A sale is one transaction:
///
///   require the seller's open till -> read products under the write lock ->
///   price and validate every line -> create sale + lines -> issue stock ->
///   post the journal entry -> commit
///
/// If any step fails none of it happened: no invoice number burned, no stock
/// moved, no ledger entry. Totals are always computed here from the product
/// records - a total arriving from the screen is never trusted.
///
/// A sale is never edited or deleted. A mistake is corrected with a refund: its
/// own document, with its own journal entry and stock movements.
/// </summary>
public sealed class SaleService
{
    private readonly DbSession _db;
    private readonly IClock _clock;
    private readonly AccountingService _accounting;
    private readonly InventoryService _inventory;
    private readonly ShiftService _shifts;
    private readonly SequenceService _sequences;
    private readonly SettingsService _settings;
    private readonly PartyService _parties;

    public SaleService(
        DbSession db,
        IClock clock,
        AccountingService accounting,
        InventoryService inventory,
        ShiftService shifts,
        SequenceService sequences,
        SettingsService settings,
        PartyService parties)
    {
        _db = db;
        _clock = clock;
        _accounting = accounting;
        _inventory = inventory;
        _shifts = shifts;
        _sequences = sequences;
        _settings = settings;
        _parties = parties;
    }

    /// <summary>
    /// Prices a cart without saving anything, for the till's running total.
    /// Uses exactly the arithmetic checkout uses, so the screen and the invoice
    /// can never disagree.
    /// </summary>
    public CartQuote Quote(IReadOnlyList<CartLine> lines)
    {
        var products = LoadProducts(lines);
        return Price(lines, products, _settings.GetTaxRate(), _settings.GetPricesIncludeTax(), planStock: false);
    }

    /// <summary>Rings up a sale.</summary>
    public Sale Checkout(
        Actor seller,
        IReadOnlyList<CartLine> lines,
        PaymentMethod paymentMethod,
        Money? amountTendered = null,
        long? customerId = null,
        string? notes = null)
    {
        seller.Require(Permission.SaleCreate, "ليست لديك صلاحية البيع.");

        if (lines.Count == 0)
        {
            throw new BusinessRuleException("السلة فارغة. أضف صنفاً واحداً على الأقل.");
        }

        // The main guardrail, before anything else is touched.
        var session = _shifts.RequireOpenSessionFor(seller.Id);
        var saleDate = _clock.Today;
        _accounting.ResolveOpenPeriodFor(saleDate);

        var taxRate = _settings.GetTaxRate();
        var pricesIncludeTax = _settings.GetPricesIncludeTax();

        return _db.InTransaction(() =>
        {
            // Read under the write lock: these figures cannot change before commit.
            var products = LoadProducts(lines);
            var quote = Price(lines, products, taxRate, pricesIncludeTax, planStock: true);

            var (paid, change) = Settle(paymentMethod, quote.Total, amountTendered);

            if (paymentMethod.IsDeferred())
            {
                AssertCreditAvailable(customerId, quote.Total);
            }
            else if (customerId is not null)
            {
                _parties.GetCustomer(customerId.Value);
            }

            var invoiceNumber = _sequences.Next(SequenceService.Invoice, saleDate.Year);

            var saleId = _db.Insert(
                """
                INSERT INTO sales
                    (invoice_number, sale_date, sold_at, customer_id, user_id, shift_session_id,
                     prices_include_tax, tax_rate_bp, subtotal, discount_total, tax_total, total, cogs_total,
                     payment_method, amount_paid, change_due, status, notes)
                VALUES
                    (@invoiceNumber, @saleDate, @now, @customerId, @userId, @sessionId,
                     @pricesIncludeTax, @taxRateBp, @Subtotal, @DiscountTotal, @TaxTotal, @Total, @CogsTotal,
                     @method, @paid, @change, 'Completed', @notes)
                RETURNING id
                """,
                new
                {
                    invoiceNumber,
                    saleDate,
                    now = _clock.Now,
                    customerId,
                    userId = seller.Id,
                    sessionId = session.Id,
                    pricesIncludeTax,
                    taxRateBp = ToBasisPoints(taxRate),
                    quote.Subtotal,
                    quote.DiscountTotal,
                    quote.TaxTotal,
                    quote.Total,
                    quote.CogsTotal,
                    method = paymentMethod.ToString(),
                    paid,
                    change,
                    notes = Clean(notes),
                });

            foreach (var line in quote.Lines)
            {
                var product = products[line.ProductId];

                _db.Execute(
                    """
                    INSERT INTO sale_items
                        (sale_id, product_id, sku, name, unit, quantity, unit_price, discount, line_total, unit_cost, line_cogs)
                    VALUES
                        (@saleId, @ProductId, @sku, @name, @unit, @Quantity, @UnitPrice, @Discount, @LineTotal, @UnitCost, @LineCogs)
                    """,
                    new
                    {
                        saleId,
                        line.ProductId,
                        sku = product.Sku,
                        name = product.Name,
                        unit = product.Unit,
                        line.Quantity,
                        line.UnitPrice,
                        line.Discount,
                        line.LineTotal,
                        line.UnitCost,
                        line.LineCogs,
                    });

                // Throws on oversell, rolling back the whole sale - invoice
                // number included.
                var movement = _inventory.Issue(line.ProductId, line.Quantity, StockMovementType.Sale, seller,
                    nameof(Sale), saleId, $"بيع بالفاتورة {invoiceNumber}");

                // The plan and the movement use the same valuation; if they ever
                // disagreed the COGS on the invoice would not match the ledger.
                if (-movement.ValueChange != line.LineCogs)
                {
                    throw new InvalidOperationException(
                        $"COGS mismatch on {product.Sku}: planned {line.LineCogs}, issued {-movement.ValueChange}.");
                }
            }

            var entry = PostSale(invoiceNumber, saleId, paymentMethod, quote, saleDate, seller);

            if (entry is not null)
            {
                _db.Execute("UPDATE sales SET journal_entry_id = @entryId WHERE id = @saleId", new { saleId, entryId = entry.Id });
            }

            return GetSale(saleId);
        });
    }

    /// <summary>
    /// Returns part or all of a sale. The invoice keeps its figures; this
    /// creates a refund document with its own entry and, for goods that come
    /// back on the shelf, inbound stock at the cost they left at.
    /// </summary>
    public SaleRefund Refund(
        Actor actor,
        long saleId,
        IReadOnlyList<RefundLineRequest> requests,
        string reason,
        PaymentMethod? paymentMethod = null)
    {
        actor.Require(Permission.SaleRefund, "ليست لديك صلاحية الاسترجاع.");

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new BusinessRuleException("الاسترجاع يتطلب ذكر السبب.");
        }

        if (requests.Count == 0 || requests.All(r => !r.Quantity.IsPositive))
        {
            throw new BusinessRuleException("حدد صنفاً واحداً على الأقل للاسترجاع.");
        }

        var session = _shifts.RequireOpenSessionFor(actor.Id);
        var refundDate = _clock.Today;
        _accounting.ResolveOpenPeriodFor(refundDate);

        return _db.InTransaction(() =>
        {
            var sale = GetSale(saleId);
            var items = GetItems(saleId).ToDictionary(i => i.Id);

            var method = paymentMethod ?? sale.PaymentMethod;

            // A credit invoice is refunded against the customer's account; a paid
            // one can never be turned into a debt.
            if ((sale.PaymentMethod == PaymentMethod.Credit) != (method == PaymentMethod.Credit))
            {
                throw new BusinessRuleException(sale.PaymentMethod == PaymentMethod.Credit
                    ? "الفاتورة آجلة، فيُخصم المرتجع من حساب العميل ولا يُرد نقداً."
                    : "لا يمكن رد مبلغ فاتورة مدفوعة كدين آجل.");
            }

            var lines = new List<PlannedRefundLine>();

            foreach (var request in requests.Where(r => r.Quantity.IsPositive))
            {
                if (!items.TryGetValue(request.SaleItemId, out var item))
                {
                    throw new BusinessRuleException("أحد الأصناف المحددة لا ينتمي لهذه الفاتورة.");
                }

                if (lines.Any(l => l.Item.Id == item.Id))
                {
                    throw new BusinessRuleException($"الصنف {item.Sku} مكرر في طلب الاسترجاع.");
                }

                if (request.Quantity > item.RefundableQuantity)
                {
                    throw new BusinessRuleException(
                        $"لا يمكن استرجاع {request.Quantity} من الصنف {item.Sku}؛ المتبقي القابل للاسترجاع {item.RefundableQuantity} فقط.");
                }

                // The last units of a line take exactly what remains of it, so
                // a line refunded in parts always sums back to the original.
                var isRemainder = request.Quantity == item.RefundableQuantity;

                var amount = isRemainder
                    ? item.LineTotal - item.RefundedAmount
                    : Money.Of(item.LineTotal.Amount * request.Quantity.Value / item.Quantity.Value);

                var cogs = isRemainder
                    ? item.LineCogs - item.RefundedCogs
                    : Money.Of(item.LineCogs.Amount * request.Quantity.Value / item.Quantity.Value);

                lines.Add(new PlannedRefundLine(item, request.Quantity, amount, cogs, request.Restock));
            }

            var subtotal = lines.Sum(l => l.Amount);

            // Does this refund return everything still outstanding on the invoice?
            var completesSale = items.Values.All(i =>
                (lines.FirstOrDefault(l => l.Item.Id == i.Id)?.Quantity ?? Quantity.Zero) == i.RefundableQuantity);

            var remainingTax = sale.TaxTotal - sale.RefundedTax;

            var tax = completesSale
                ? remainingTax
                : sale.NetOfDiscount.IsZero
                    ? Money.Zero
                    : Money.Min(remainingTax, Money.Of(sale.TaxTotal.Amount * subtotal.Amount / sale.NetOfDiscount.Amount));

            var total = sale.PricesIncludeTax ? subtotal : subtotal + tax;

            if (sale.RefundedTotal + total > sale.Total)
            {
                throw new BusinessRuleException($"هذا الاسترجاع يتجاوز إجمالي الفاتورة ({sale.Total.ToDisplay()}).");
            }

            var cogsTotal = lines.Sum(l => l.Cogs);
            var restockedCogs = lines.Where(l => l.Restock).Sum(l => l.Cogs);
            var refundNumber = _sequences.Next(SequenceService.Refund, refundDate.Year);

            var refundId = _db.Insert(
                """
                INSERT INTO sale_refunds
                    (refund_number, sale_id, refund_date, refunded_at, user_id, shift_session_id, prices_include_tax,
                     subtotal, tax_total, total, cogs_total, restocked_cogs, payment_method, reason)
                VALUES
                    (@refundNumber, @saleId, @refundDate, @now, @userId, @sessionId, @pricesIncludeTax,
                     @subtotal, @tax, @total, @cogsTotal, @restockedCogs, @method, @reason)
                RETURNING id
                """,
                new
                {
                    refundNumber,
                    saleId,
                    refundDate,
                    now = _clock.Now,
                    userId = actor.Id,
                    sessionId = session.Id,
                    pricesIncludeTax = sale.PricesIncludeTax,
                    subtotal,
                    tax,
                    total,
                    cogsTotal,
                    restockedCogs,
                    method = method.ToString(),
                    reason = reason.Trim(),
                });

            foreach (var line in lines)
            {
                _db.Execute(
                    """
                    INSERT INTO sale_refund_items (sale_refund_id, sale_item_id, product_id, quantity, line_total, line_cogs, restocked)
                    VALUES (@refundId, @itemId, @productId, @Quantity, @Amount, @Cogs, @Restock)
                    """,
                    new { refundId, itemId = line.Item.Id, productId = line.Item.ProductId, line.Quantity, line.Amount, line.Cogs, line.Restock });

                _db.Execute(
                    """
                    UPDATE sale_items SET
                        refunded_quantity = refunded_quantity + @Quantity,
                        refunded_amount = refunded_amount + @Amount,
                        refunded_cogs = refunded_cogs + @Cogs
                    WHERE id = @itemId
                    """,
                    new { itemId = line.Item.Id, line.Quantity, line.Amount, line.Cogs });

                if (line.Restock)
                {
                    // Back on the shelf at the cost it left at, so Inventory and
                    // the shelf value stay in step.
                    _inventory.Receive(line.Item.ProductId, line.Quantity, line.Cogs, StockMovementType.SaleReturn, actor,
                        nameof(SaleRefund), refundId, $"مرتجع بالسند {refundNumber}");
                }
            }

            _db.Execute(
                """
                UPDATE sales SET refunded_total = refunded_total + @total, refunded_tax = refunded_tax + @tax, status = @status
                WHERE id = @saleId
                """,
                new
                {
                    saleId,
                    total,
                    tax,
                    status = (completesSale ? SaleStatus.Refunded : SaleStatus.PartiallyRefunded).ToString(),
                });

            var entry = PostRefund(refundNumber, sale.InvoiceNumber, refundId, method, total, tax, restockedCogs, refundDate, actor);

            if (entry is not null)
            {
                _db.Execute("UPDATE sale_refunds SET journal_entry_id = @entryId WHERE id = @refundId", new { refundId, entryId = entry.Id });
            }

            return GetRefund(refundId);
        });
    }

    /// <summary>Returns everything still outstanding on an invoice.</summary>
    public SaleRefund RefundInFull(Actor actor, long saleId, string reason, bool restock = true)
    {
        var requests = GetItems(saleId)
            .Where(i => i.RefundableQuantity.IsPositive)
            .Select(i => new RefundLineRequest(i.Id, i.RefundableQuantity, restock))
            .ToList();

        if (requests.Count == 0)
        {
            throw new BusinessRuleException("هذه الفاتورة مسترجعة بالكامل من قبل.");
        }

        return Refund(actor, saleId, requests, reason);
    }

    // --- Reads ------------------------------------------------------------------

    public Sale GetSale(long id) =>
        _db.QueryFirstOrDefault<Sale>("SELECT * FROM sales WHERE id = @id", new { id })
        ?? throw new BusinessRuleException("الفاتورة غير موجودة.");

    public Sale? FindByInvoiceNumber(string invoiceNumber) =>
        _db.QueryFirstOrDefault<Sale>("SELECT * FROM sales WHERE invoice_number = @invoiceNumber COLLATE NOCASE",
            new { invoiceNumber = invoiceNumber.Trim() });

    public IReadOnlyList<SaleItem> GetItems(long saleId) =>
        _db.Query<SaleItem>("SELECT * FROM sale_items WHERE sale_id = @saleId ORDER BY id", new { saleId }).ToList();

    public SaleRefund GetRefund(long id) =>
        _db.QueryFirstOrDefault<SaleRefund>("SELECT * FROM sale_refunds WHERE id = @id", new { id })
        ?? throw new BusinessRuleException("سند الاسترجاع غير موجود.");

    public IReadOnlyList<SaleRefund> RefundsFor(long saleId) =>
        _db.Query<SaleRefund>("SELECT * FROM sale_refunds WHERE sale_id = @saleId ORDER BY id", new { saleId }).ToList();

    public IReadOnlyList<SaleRefundItem> GetRefundItems(long refundId) =>
        _db.Query<SaleRefundItem>("SELECT * FROM sale_refund_items WHERE sale_refund_id = @refundId ORDER BY id", new { refundId }).ToList();

    /// <summary>Sales list. Without view-all permission, only the viewer's own sales.</summary>
    public IReadOnlyList<SaleRow> Sales(Actor viewer, SaleFilter filter)
    {
        var userId = viewer.Can(Permission.SaleViewAll) ? filter.UserId : viewer.Id;

        return _db.Query<SaleRow>(
            """
            SELECT s.id, s.invoice_number, s.sale_date, s.sold_at, s.total, s.tax_total, s.cogs_total,
                   s.payment_method, s.status, s.refunded_total, s.user_id, s.shift_session_id,
                   u.full_name AS seller_name, c.name AS customer_name,
                   (SELECT COUNT(*) FROM sale_items i WHERE i.sale_id = s.id) AS item_count
            FROM sales s
            JOIN users u ON u.id = s.user_id
            LEFT JOIN customers c ON c.id = s.customer_id
            WHERE (@from IS NULL OR s.sale_date >= @from)
              AND (@to IS NULL OR s.sale_date <= @to)
              AND (@userId IS NULL OR s.user_id = @userId)
              AND (@customerId IS NULL OR s.customer_id = @customerId)
              AND (@method IS NULL OR s.payment_method = @method)
              AND (@search IS NULL OR s.invoice_number LIKE @like OR c.name LIKE @like)
            ORDER BY s.id DESC
            LIMIT 5000
            """,
            new
            {
                // Named explicitly: SQLite binds parameters case-sensitively, so
                // "From" from the record would never match "@from" in the SQL.
                from = filter.From,
                to = filter.To,
                userId,
                customerId = filter.CustomerId,
                method = filter.PaymentMethod?.ToString(),
                search = string.IsNullOrWhiteSpace(filter.Search) ? null : filter.Search.Trim(),
                like = $"%{filter.Search?.Trim()}%",
            }).ToList();
    }

    public IReadOnlyList<RefundRow> Refunds(Actor viewer, DateOnly? from, DateOnly? to)
    {
        var userId = viewer.Can(Permission.SaleViewAll) ? (long?)null : viewer.Id;

        return _db.Query<RefundRow>(
            """
            SELECT r.id, r.refund_number, r.refund_date, r.total, r.payment_method, r.reason,
                   s.invoice_number, u.full_name AS user_name, r.sale_id
            FROM sale_refunds r
            JOIN sales s ON s.id = r.sale_id
            JOIN users u ON u.id = r.user_id
            WHERE (@from IS NULL OR r.refund_date >= @from)
              AND (@to IS NULL OR r.refund_date <= @to)
              AND (@userId IS NULL OR r.user_id = @userId)
            ORDER BY r.id DESC
            """,
            new { from, to, userId }).ToList();
    }

    // --- Pricing ------------------------------------------------------------------

    private Dictionary<long, Product> LoadProducts(IReadOnlyList<CartLine> lines)
    {
        var ids = lines.Select(l => l.ProductId).Distinct().ToArray();

        var products = _db.Query<Product>("SELECT * FROM products WHERE id IN @ids", new { ids })
            .ToDictionary(p => p.Id);

        foreach (var id in ids)
        {
            if (!products.ContainsKey(id))
            {
                throw new BusinessRuleException($"الصنف رقم {id} غير موجود.");
            }
        }

        return products;
    }

    /// <summary>
    /// The one place a cart is priced.
    ///
    /// Exclusive pricing: tax = net x rate, total = net + tax.
    /// Inclusive pricing: the shelf price is what the customer pays, so
    /// total = net and tax = net x rate / (1 + rate) is carved out of it.
    /// </summary>
    private static CartQuote Price(
        IReadOnlyList<CartLine> lines,
        IReadOnlyDictionary<long, Product> products,
        decimal taxRate,
        bool pricesIncludeTax,
        bool planStock)
    {
        // Running stock per product, so the same part twice in a cart is
        // checked and costed against what the first line leaves behind.
        var running = products.Values.ToDictionary(p => p.Id, p => (Qty: p.StockQty, Value: p.StockValue));
        var priced = new List<QuotedLine>();
        var warnings = new List<string>();

        foreach (var line in lines)
        {
            var product = products[line.ProductId];

            if (!line.Quantity.IsPositive)
            {
                throw new BusinessRuleException($"كمية الصنف {product.Sku} يجب أن تكون أكبر من صفر.");
            }

            if (line.Discount.IsNegative)
            {
                throw new BusinessRuleException($"الخصم على الصنف {product.Sku} لا يمكن أن يكون سالباً.");
            }

            if (!product.IsActive)
            {
                throw new BusinessRuleException($"الصنف {product.Sku} موقوف ولا يمكن بيعه.");
            }

            var unitPrice = line.UnitPrice ?? product.SellingPrice;

            if (unitPrice.IsNegative)
            {
                throw new BusinessRuleException($"سعر الصنف {product.Sku} لا يمكن أن يكون سالباً.");
            }

            var gross = unitPrice * line.Quantity;

            if (line.Discount > gross)
            {
                throw new BusinessRuleException(
                    $"الخصم على الصنف {product.Sku} ({line.Discount.ToDisplay()}) يتجاوز قيمة السطر ({gross.ToDisplay()}).");
            }

            var stock = running[product.Id];

            if (line.Quantity > stock.Qty)
            {
                if (planStock)
                {
                    throw new InsufficientStockException(product.Sku, line.Quantity, stock.Qty);
                }

                warnings.Add($"{product.Sku}: المتوفر {stock.Qty} {product.Unit} فقط.");
            }

            var take = Quantity.Min(line.Quantity, stock.Qty);
            var cogs = take.IsPositive
                ? InventoryService.ValueOf(new Product { StockQty = stock.Qty, StockValue = stock.Value }, take)
                : Money.Zero;

            running[product.Id] = (stock.Qty - take, stock.Value - cogs);

            priced.Add(new QuotedLine(
                product.Id,
                product.Sku,
                product.Name,
                product.Unit,
                line.Quantity,
                unitPrice,
                line.Discount,
                gross,
                gross - line.Discount,
                line.Quantity.IsPositive ? Money.Of(cogs.Amount / line.Quantity.Value) : Money.Zero,
                cogs,
                stock.Qty));
        }

        var subtotal = priced.Sum(l => l.Gross);
        var discountTotal = priced.Sum(l => l.Discount);
        var net = subtotal - discountTotal;

        Money tax;
        Money total;

        if (pricesIncludeTax)
        {
            tax = Money.Of(net.Amount * taxRate / (1m + taxRate));
            total = net;
        }
        else
        {
            tax = net * taxRate;
            total = net + tax;
        }

        return new CartQuote(priced, subtotal, discountTotal, tax, total, priced.Sum(l => l.LineCogs), pricesIncludeTax, taxRate, warnings);
    }

    private static (Money Paid, Money Change) Settle(PaymentMethod method, Money total, Money? tendered)
    {
        if (method.IsDeferred())
        {
            // Nothing changes hands now; the balance sits in receivables.
            return (Money.Zero, Money.Zero);
        }

        if (!method.AffectsCashDrawer())
        {
            // Card and transfer settle for the exact amount.
            return (total, Money.Zero);
        }

        var paid = tendered ?? total;

        if (paid < total)
        {
            throw new BusinessRuleException(
                $"المبلغ المدفوع ({paid.ToDisplay()}) أقل من إجمالي الفاتورة ({total.ToDisplay()}).");
        }

        return (paid, paid - total);
    }

    private void AssertCreditAvailable(long? customerId, Money total)
    {
        if (customerId is null)
        {
            throw new BusinessRuleException("البيع الآجل يتطلب اختيار عميل.");
        }

        var customer = _parties.GetCustomer(customerId.Value);

        if (!customer.IsActive)
        {
            throw new BusinessRuleException($"العميل {customer.Name} موقوف.");
        }

        if (customer.CreditLimit.IsZero)
        {
            throw new BusinessRuleException($"العميل {customer.Name} عميل نقدي فقط (حد الائتمان صفر).");
        }

        var balance = _parties.CustomerBalance(customer.Id);

        if (balance + total > customer.CreditLimit)
        {
            throw new BusinessRuleException(
                $"تجاوز حد الائتمان للعميل {customer.Name}: الرصيد الحالي {balance.ToDisplay()}، والحد {customer.CreditLimit.ToDisplay()}.");
        }
    }

    // --- Ledger -------------------------------------------------------------------

    /// <summary>
    /// Dr whatever received the money, Cr revenue and tax, and move the cost out
    /// of Inventory into COGS in the same entry (perpetual inventory).
    /// </summary>
    private JournalEntry? PostSale(string invoiceNumber, long saleId, PaymentMethod method, CartQuote quote, DateOnly date, Actor seller)
    {
        var lines = new List<LedgerLine>();
        var revenue = quote.Total - quote.TaxTotal;

        if (quote.Total.IsPositive)
        {
            lines.Add(LedgerLine.Debit(SettlementAccount(method), quote.Total, "قيمة الفاتورة"));
        }

        if (revenue.IsPositive)
        {
            lines.Add(LedgerLine.Credit(AccountCodes.SalesRevenue, revenue, "مبيعات قطع غيار"));
        }

        if (quote.TaxTotal.IsPositive)
        {
            lines.Add(LedgerLine.Credit(AccountCodes.TaxPayable, quote.TaxTotal, "ضريبة المخرجات"));
        }

        if (quote.CogsTotal.IsPositive)
        {
            lines.Add(LedgerLine.Debit(AccountCodes.CostOfGoodsSold, quote.CogsTotal, "تكلفة القطع المباعة"));
            lines.Add(LedgerLine.Credit(AccountCodes.Inventory, quote.CogsTotal, "صرف من المخزون"));
        }

        // A fully discounted sale of a free item moves nothing.
        return lines.Count == 0
            ? null
            : _accounting.Post($"فاتورة مبيعات {invoiceNumber}", lines, JournalSource.Sale, date, seller, nameof(Sale), saleId);
    }

    /// <summary>
    /// The mirror: revenue is reduced through the Sales Returns contra account
    /// rather than by debiting Sales Revenue, so gross sales stay visible. Only
    /// goods that came back reduce COGS; anything written off stays a cost.
    /// </summary>
    private JournalEntry? PostRefund(
        string refundNumber,
        string invoiceNumber,
        long refundId,
        PaymentMethod method,
        Money total,
        Money tax,
        Money restockedCogs,
        DateOnly date,
        Actor actor)
    {
        var lines = new List<LedgerLine>();
        var revenue = total - tax;

        if (revenue.IsPositive)
        {
            lines.Add(LedgerLine.Debit(AccountCodes.SalesReturns, revenue, "قطع مرتجعة"));
        }

        if (tax.IsPositive)
        {
            lines.Add(LedgerLine.Debit(AccountCodes.TaxPayable, tax, "ضريبة مستردة"));
        }

        if (total.IsPositive)
        {
            lines.Add(LedgerLine.Credit(SettlementAccount(method), total, "المبلغ المرتجع"));
        }

        if (restockedCogs.IsPositive)
        {
            lines.Add(LedgerLine.Debit(AccountCodes.Inventory, restockedCogs, "إعادة القطع للمخزون"));
            lines.Add(LedgerLine.Credit(AccountCodes.CostOfGoodsSold, restockedCogs, "تكلفة القطع المرتجعة"));
        }

        return lines.Count == 0
            ? null
            : _accounting.Post($"مرتجع {refundNumber} على الفاتورة {invoiceNumber}", lines, JournalSource.SaleRefund,
                date, actor, nameof(SaleRefund), refundId);
    }

    internal static string SettlementAccount(PaymentMethod method) => method switch
    {
        PaymentMethod.Cash => AccountCodes.Cash,
        PaymentMethod.Card or PaymentMethod.Transfer => AccountCodes.Bank,
        PaymentMethod.Credit => AccountCodes.AccountsReceivable,
        _ => throw new ArgumentOutOfRangeException(nameof(method)),
    };

    internal static int ToBasisPoints(decimal rate) => decimal.ToInt32(Math.Round(rate * 10000m, MidpointRounding.AwayFromZero));

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record PlannedRefundLine(SaleItem Item, Quantity Quantity, Money Amount, Money Cogs, bool Restock);
}

/// <summary>One line of a till cart. A null price means the product's current selling price.</summary>
public sealed record CartLine(long ProductId, Quantity Quantity, Money? UnitPrice = null, Money Discount = default);

public sealed record RefundLineRequest(long SaleItemId, Quantity Quantity, bool Restock = true);

public sealed record QuotedLine(
    long ProductId,
    string Sku,
    string Name,
    string Unit,
    Quantity Quantity,
    Money UnitPrice,
    Money Discount,
    Money Gross,
    Money LineTotal,
    Money UnitCost,
    Money LineCogs,
    Quantity Available);

public sealed record CartQuote(
    IReadOnlyList<QuotedLine> Lines,
    Money Subtotal,
    Money DiscountTotal,
    Money TaxTotal,
    Money Total,
    Money CogsTotal,
    bool PricesIncludeTax,
    decimal TaxRate,
    IReadOnlyList<string> Warnings);

public sealed record SaleFilter(
    DateOnly? From = null,
    DateOnly? To = null,
    long? UserId = null,
    long? CustomerId = null,
    PaymentMethod? PaymentMethod = null,
    string? Search = null);

public sealed class SaleRow
{
    public long Id { get; set; }
    public string InvoiceNumber { get; set; } = "";
    public DateOnly SaleDate { get; set; }
    public DateTime SoldAt { get; set; }
    public Money Total { get; set; }
    public Money TaxTotal { get; set; }
    public Money CogsTotal { get; set; }
    public PaymentMethod PaymentMethod { get; set; }
    public SaleStatus Status { get; set; }
    public Money RefundedTotal { get; set; }
    public long UserId { get; set; }
    public long ShiftSessionId { get; set; }
    public string SellerName { get; set; } = "";
    public string? CustomerName { get; set; }
    public long ItemCount { get; set; }
}

public sealed class RefundRow
{
    public long Id { get; set; }
    public string RefundNumber { get; set; } = "";
    public DateOnly RefundDate { get; set; }
    public Money Total { get; set; }
    public PaymentMethod PaymentMethod { get; set; }
    public string Reason { get; set; } = "";
    public string InvoiceNumber { get; set; } = "";
    public string UserName { get; set; } = "";
    public long SaleId { get; set; }
}
