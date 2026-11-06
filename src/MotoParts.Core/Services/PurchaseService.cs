using MotoParts.Core.Common;
using MotoParts.Core.Data;
using MotoParts.Core.Domain;
using MotoParts.Core.Security;

namespace MotoParts.Core.Services;

/// <summary>
/// Goods in.
///
/// A purchase is drafted, edited as often as needed, then received. Receiving
/// is the one step with consequences, and it is one transaction:
///
///   write inbound stock movements (carrying exactly what each line cost) ->
///   post Dr Inventory + Dr input tax / Cr Payables or Cash/Bank -> commit
///
/// Inventory is debited with the same line values the movements carry, so the
/// Inventory account and the shelf value move together.
/// </summary>
public sealed class PurchaseService
{
    private readonly DbSession _db;
    private readonly IClock _clock;
    private readonly AccountingService _accounting;
    private readonly InventoryService _inventory;
    private readonly SequenceService _sequences;
    private readonly AuditService _audit;

    public PurchaseService(DbSession db, IClock clock, AccountingService accounting, InventoryService inventory, SequenceService sequences, AuditService audit)
    {
        _db = db;
        _clock = clock;
        _accounting = accounting;
        _inventory = inventory;
        _sequences = sequences;
        _audit = audit;
    }

    public Purchase Get(long id) =>
        _db.QueryFirstOrDefault<Purchase>("SELECT * FROM purchases WHERE id = @id", new { id })
        ?? throw new BusinessRuleException("أمر الشراء غير موجود.");

    public IReadOnlyList<PurchaseItem> Items(long purchaseId) =>
        _db.Query<PurchaseItem>("SELECT * FROM purchase_items WHERE purchase_id = @purchaseId ORDER BY id", new { purchaseId }).ToList();

    public IReadOnlyList<PurchaseRow> Purchases(DateOnly? from = null, DateOnly? to = null, long? supplierId = null, PurchaseStatus? status = null) =>
        _db.Query<PurchaseRow>(
            """
            SELECT p.id, p.purchase_number, p.purchase_date, p.supplier_invoice_number, p.subtotal, p.tax_total,
                   p.total, p.status, p.payment_method, p.supplier_id, s.name AS supplier_name,
                   (SELECT COUNT(*) FROM purchase_items i WHERE i.purchase_id = p.id) AS item_count
            FROM purchases p JOIN suppliers s ON s.id = p.supplier_id
            WHERE (@from IS NULL OR p.purchase_date >= @from)
              AND (@to IS NULL OR p.purchase_date <= @to)
              AND (@supplierId IS NULL OR p.supplier_id = @supplierId)
              AND (@status IS NULL OR p.status = @status)
            ORDER BY p.id DESC
            """,
            new { from, to, supplierId, status = status?.ToString() }).ToList();

    /// <summary>Drafts a purchase order. Nothing moves until it is received.</summary>
    public Purchase Draft(Actor buyer, PurchaseInput input)
    {
        buyer.Require(Permission.PurchaseManage, "ليست لديك صلاحية إنشاء أوامر الشراء.");
        Validate(input);

        return _db.InTransaction(() =>
        {
            var supplier = _db.QueryFirstOrDefault<Supplier>("SELECT * FROM suppliers WHERE id = @id", new { id = input.SupplierId })
                ?? throw new BusinessRuleException("المورد غير موجود.");

            if (!supplier.IsActive)
            {
                throw new BusinessRuleException($"المورد {supplier.Name} موقوف.");
            }

            var number = _sequences.Next(SequenceService.Purchase, input.PurchaseDate.Year);

            var id = _db.Insert(
                """
                INSERT INTO purchases
                    (purchase_number, supplier_id, user_id, purchase_date, supplier_invoice_number, tax_rate_bp,
                     status, payment_method, notes, created_at, updated_at)
                VALUES
                    (@number, @SupplierId, @userId, @PurchaseDate, @invoice, @taxRateBp,
                     'Draft', @method, @notes, @now, @now)
                RETURNING id
                """,
                new
                {
                    number,
                    input.SupplierId,
                    userId = buyer.Id,
                    input.PurchaseDate,
                    invoice = Clean(input.SupplierInvoiceNumber),
                    taxRateBp = SaleService.ToBasisPoints(input.TaxRate),
                    method = input.PaymentMethod.ToString(),
                    notes = Clean(input.Notes),
                    now = _clock.Now,
                });

            WriteLines(id, input);
            _audit.Record(nameof(Purchase), id, AuditAction.Created, buyer, newValues: new { number, input.SupplierId });

            return Get(id);
        });
    }

    /// <summary>Replaces a draft's header and lines.</summary>
    public Purchase UpdateDraft(Actor buyer, long purchaseId, PurchaseInput input)
    {
        buyer.Require(Permission.PurchaseManage, "ليست لديك صلاحية تعديل أوامر الشراء.");
        Validate(input);

        return _db.InTransaction(() =>
        {
            var purchase = Get(purchaseId);

            if (purchase.Status != PurchaseStatus.Draft)
            {
                throw new BusinessRuleException("لا يمكن تعديل أمر شراء مستلم أو ملغى.");
            }

            if (purchase.PurchaseDate.Year != input.PurchaseDate.Year)
            {
                throw new BusinessRuleException("لا يمكن نقل أمر الشراء إلى سنة أخرى لأن رقمه مرتبط بسنته.");
            }

            _db.Execute("DELETE FROM purchase_items WHERE purchase_id = @purchaseId", new { purchaseId });

            _db.Execute(
                """
                UPDATE purchases SET supplier_id = @SupplierId, purchase_date = @PurchaseDate,
                    supplier_invoice_number = @invoice, tax_rate_bp = @taxRateBp, payment_method = @method,
                    notes = @notes, updated_at = @now
                WHERE id = @purchaseId
                """,
                new
                {
                    purchaseId,
                    input.SupplierId,
                    input.PurchaseDate,
                    invoice = Clean(input.SupplierInvoiceNumber),
                    taxRateBp = SaleService.ToBasisPoints(input.TaxRate),
                    method = input.PaymentMethod.ToString(),
                    notes = Clean(input.Notes),
                    now = _clock.Now,
                });

            WriteLines(purchaseId, input);
            _audit.Record(nameof(Purchase), purchaseId, AuditAction.Updated, buyer);

            return Get(purchaseId);
        });
    }

    /// <summary>Receives a draft: stock arrives and the ledger is posted.</summary>
    public Purchase Receive(Actor receiver, long purchaseId)
    {
        receiver.Require(Permission.PurchaseReceive, "ليست لديك صلاحية استلام المشتريات.");

        return _db.InTransaction(() =>
        {
            var purchase = Get(purchaseId);

            if (purchase.Status == PurchaseStatus.Received)
            {
                throw new BusinessRuleException($"أمر الشراء {purchase.PurchaseNumber} مستلم من قبل.");
            }

            if (purchase.Status == PurchaseStatus.Cancelled)
            {
                throw new BusinessRuleException($"أمر الشراء {purchase.PurchaseNumber} ملغى.");
            }

            _accounting.ResolveOpenPeriodFor(purchase.PurchaseDate);

            var items = Items(purchaseId);

            if (items.Count == 0)
            {
                throw new BusinessRuleException("أمر الشراء لا يحتوي على أصناف.");
            }

            // A received purchase must carry a ledger entry, and an entry cannot
            // be for zero. Free samples go in as an opening/adjustment instead.
            if (purchase.Total.IsZero)
            {
                throw new BusinessRuleException("لا يمكن استلام أمر شراء قيمته صفر؛ أدخل العينات المجانية كتسوية مخزون.");
            }

            foreach (var item in items)
            {
                _inventory.Receive(item.ProductId, item.Quantity, item.LineTotal, StockMovementType.Purchase, receiver,
                    nameof(Purchase), purchaseId, $"استلام بأمر الشراء {purchase.PurchaseNumber}");
            }

            var supplierName = _db.ExecuteScalar<string>("SELECT name FROM suppliers WHERE id = @id", new { id = purchase.SupplierId });

            var lines = new List<LedgerLine>();

            if (purchase.Subtotal.IsPositive)
            {
                lines.Add(LedgerLine.Debit(AccountCodes.Inventory, purchase.Subtotal, "قطع مستلمة للمخزون"));
            }

            if (purchase.TaxTotal.IsPositive)
            {
                // Input tax offsets what is owed on sales.
                lines.Add(LedgerLine.Debit(AccountCodes.TaxPayable, purchase.TaxTotal, "ضريبة المدخلات"));
            }

            lines.Add(LedgerLine.Credit(SettlementAccount(purchase.PaymentMethod), purchase.Total, supplierName));

            var entryId = _accounting.Post($"مشتريات {purchase.PurchaseNumber} من {supplierName}", lines,
                JournalSource.Purchase, purchase.PurchaseDate, receiver, nameof(Purchase), purchaseId).Id;

            _db.Execute(
                """
                UPDATE purchases SET status = 'Received', received_at = @now, received_by = @receiverId,
                    journal_entry_id = @entryId, updated_at = @now
                WHERE id = @purchaseId
                """,
                new { purchaseId, now = _clock.Now, receiverId = receiver.Id, entryId });

            _audit.Record(nameof(Purchase), purchaseId, AuditAction.Received, receiver, newValues: new { purchase.Total });

            return Get(purchaseId);
        });
    }

    /// <summary>Drafts and receives in one step, for over-the-counter buying.</summary>
    public Purchase DraftAndReceive(Actor buyer, PurchaseInput input) =>
        _db.InTransaction(() => Receive(buyer, Draft(buyer, input).Id));

    /// <summary>Cancels a draft. A received purchase is corrected with a stock adjustment instead.</summary>
    public Purchase Cancel(Actor actor, long purchaseId, string reason)
    {
        actor.Require(Permission.PurchaseManage, "ليست لديك صلاحية إلغاء أوامر الشراء.");

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new BusinessRuleException("إلغاء أمر الشراء يتطلب ذكر السبب.");
        }

        return _db.InTransaction(() =>
        {
            var purchase = Get(purchaseId);

            if (purchase.Status != PurchaseStatus.Draft)
            {
                throw new BusinessRuleException("لا يمكن إلغاء إلا المسودات؛ أمر الشراء المستلم يُصحَّح بتسوية مخزون.");
            }

            _db.Execute(
                """
                UPDATE purchases SET status = 'Cancelled', cancelled_at = @now, cancelled_by = @actorId,
                    cancel_reason = @reason, updated_at = @now
                WHERE id = @purchaseId
                """,
                new { purchaseId, now = _clock.Now, actorId = actor.Id, reason = reason.Trim() });

            _audit.Record(nameof(Purchase), purchaseId, AuditAction.Cancelled, actor, reason: reason);

            return Get(purchaseId);
        });
    }

    private void WriteLines(long purchaseId, PurchaseInput input)
    {
        var subtotal = Money.Zero;

        foreach (var line in input.Lines)
        {
            var product = _db.QueryFirstOrDefault<Product>("SELECT * FROM products WHERE id = @id", new { id = line.ProductId })
                ?? throw new BusinessRuleException($"الصنف رقم {line.ProductId} غير موجود.");

            var lineTotal = line.UnitCost * line.Quantity;
            subtotal += lineTotal;

            _db.Execute(
                """
                INSERT INTO purchase_items (purchase_id, product_id, sku, name, quantity, unit_cost, line_total)
                VALUES (@purchaseId, @productId, @sku, @name, @Quantity, @UnitCost, @lineTotal)
                """,
                new { purchaseId, productId = product.Id, sku = product.Sku, name = product.Name, line.Quantity, line.UnitCost, lineTotal });
        }

        var tax = subtotal * input.TaxRate;

        _db.Execute(
            "UPDATE purchases SET subtotal = @subtotal, tax_total = @tax, total = @total WHERE id = @purchaseId",
            new { purchaseId, subtotal, tax, total = subtotal + tax });
    }

    private static void Validate(PurchaseInput input)
    {
        if (input.Lines.Count == 0)
        {
            throw new BusinessRuleException("أمر الشراء يجب أن يحتوي على صنف واحد على الأقل.");
        }

        SettingsService.ValidateTaxRate(input.TaxRate);

        foreach (var line in input.Lines)
        {
            if (!line.Quantity.IsPositive)
            {
                throw new BusinessRuleException("كمية كل صنف يجب أن تكون أكبر من صفر.");
            }

            if (line.UnitCost.IsNegative)
            {
                throw new BusinessRuleException("تكلفة الوحدة لا يمكن أن تكون سالبة.");
            }
        }
    }

    private static string SettlementAccount(PaymentMethod method) => method switch
    {
        PaymentMethod.Cash => AccountCodes.Cash,
        PaymentMethod.Card or PaymentMethod.Transfer => AccountCodes.Bank,
        PaymentMethod.Credit => AccountCodes.AccountsPayable,
        _ => throw new ArgumentOutOfRangeException(nameof(method)),
    };

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record PurchaseLineInput(long ProductId, Quantity Quantity, Money UnitCost);

public sealed record PurchaseInput(
    long SupplierId,
    DateOnly PurchaseDate,
    IReadOnlyList<PurchaseLineInput> Lines,
    PaymentMethod PaymentMethod = PaymentMethod.Credit,
    decimal TaxRate = 0m,
    string? SupplierInvoiceNumber = null,
    string? Notes = null);

public sealed class PurchaseRow
{
    public long Id { get; set; }
    public string PurchaseNumber { get; set; } = "";
    public DateOnly PurchaseDate { get; set; }
    public string? SupplierInvoiceNumber { get; set; }
    public Money Subtotal { get; set; }
    public Money TaxTotal { get; set; }
    public Money Total { get; set; }
    public PurchaseStatus Status { get; set; }
    public PaymentMethod PaymentMethod { get; set; }
    public long SupplierId { get; set; }
    public string SupplierName { get; set; } = "";
    public long ItemCount { get; set; }
}
