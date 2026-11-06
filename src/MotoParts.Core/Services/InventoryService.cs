using MotoParts.Core.Common;
using MotoParts.Core.Data;
using MotoParts.Core.Domain;
using MotoParts.Core.Security;

namespace MotoParts.Core.Services;

/// <summary>
/// The only code that moves stock.
///
/// Each product carries the quantity on hand and the cost VALUE of that
/// quantity, both maintained from the movement ledger by trigger. Cost is a
/// moving average, but the average is never stored and multiplied back out -
/// that is where the web version lost cents. Instead:
///
///   * a receipt adds exactly what was paid for the units;
///   * an issue removes value in proportion to the units taken, rounded once;
///   * an issue that empties the shelf removes whatever value remains.
///
/// So the Inventory account (debited at what was paid, credited at the value
/// removed) always equals the sum of product stock values to the halala.
///
/// Concurrency: every movement runs inside a BEGIN IMMEDIATE transaction, which
/// holds SQLite's write lock from the start, so the stock read below cannot
/// change before the movement commits. Two sales of the last unit cannot both
/// succeed.
/// </summary>
public sealed class InventoryService
{
    public const string ReferenceAdjustment = "StockAdjustment";

    private readonly DbSession _db;
    private readonly IClock _clock;
    private readonly AccountingService _accounting;
    private readonly AuditService _audit;

    public InventoryService(DbSession db, IClock clock, AccountingService accounting, AuditService audit)
    {
        _db = db;
        _clock = clock;
        _accounting = accounting;
        _audit = audit;
    }

    public Product GetProduct(long productId) =>
        _db.QueryFirstOrDefault<Product>("SELECT * FROM products WHERE id = @productId", new { productId })
        ?? throw new BusinessRuleException("الصنف غير موجود.");

    /// <summary>
    /// Brings stock in carrying <paramref name="value"/> - what the units cost
    /// in total. Re-averages the product's cost.
    /// </summary>
    public StockMovement Receive(
        long productId,
        Quantity quantity,
        Money value,
        StockMovementType type,
        Actor? actor,
        string? referenceType = null,
        long? referenceId = null,
        string? notes = null)
    {
        if (!type.IsInbound())
        {
            throw new InvalidOperationException($"{type} is not an inbound movement type.");
        }

        if (!quantity.IsPositive)
        {
            throw new BusinessRuleException("كمية الاستلام يجب أن تكون أكبر من صفر.");
        }

        if (value.IsNegative)
        {
            throw new BusinessRuleException("قيمة الاستلام لا يمكن أن تكون سالبة.");
        }

        return _db.InTransaction(() =>
        {
            var product = GetProduct(productId);
            var movement = Write(product, type, quantity, value, actor, referenceType, referenceId, notes);

            var newQty = product.StockQty + quantity;
            var newValue = product.StockValue + value;
            UpdateCostPrice(productId, AverageCost(newValue, newQty));

            return movement;
        });
    }

    /// <summary>
    /// Takes stock out, refusing to go below zero. Returns the movement; its
    /// (negative) <see cref="StockMovement.ValueChange"/> is the cost of the
    /// units removed, i.e. their COGS.
    /// </summary>
    public StockMovement Issue(
        long productId,
        Quantity quantity,
        StockMovementType type,
        Actor? actor,
        string? referenceType = null,
        long? referenceId = null,
        string? notes = null)
    {
        if (!type.IsOutbound())
        {
            throw new InvalidOperationException($"{type} is not an outbound movement type.");
        }

        if (!quantity.IsPositive)
        {
            throw new BusinessRuleException("الكمية المصروفة يجب أن تكون أكبر من صفر.");
        }

        return _db.InTransaction(() =>
        {
            var product = GetProduct(productId);
            EnsureAvailable(product, quantity);

            var value = ValueOf(product, quantity);
            var movement = Write(product, type, -quantity, -value, actor, referenceType, referenceId, notes);

            // Keep the displayed average honest about what is left on the shelf.
            UpdateCostPrice(productId, AverageCost(movement.ValueAfter, movement.BalanceAfter));

            return movement;
        });
    }

    /// <summary>
    /// A counted correction: positive adds, negative removes. Posts the value
    /// difference to the ledger against Inventory Adjustment, so the Inventory
    /// account follows the shelf. A reason is mandatory - an unexplained
    /// adjustment is indistinguishable from theft when the books are reviewed.
    /// </summary>
    public StockMovement Adjust(Actor actor, long productId, Quantity signedQuantity, string reason)
    {
        actor.Require(Permission.InventoryAdjustStock, "ليست لديك صلاحية تسوية المخزون.");

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new BusinessRuleException("تسوية المخزون تتطلب ذكر السبب.");
        }

        if (signedQuantity.IsZero)
        {
            throw new BusinessRuleException("لا يمكن تسوية المخزون بكمية صفر.");
        }

        _accounting.ResolveOpenPeriodFor(_clock.Today);

        return _db.InTransaction(() =>
        {
            var product = GetProduct(productId);
            StockMovement movement;

            if (signedQuantity.IsPositive)
            {
                // Found stock is valued at the current average; with nothing on
                // hand, at the last known cost.
                var value = product.StockQty.IsPositive
                    ? Money.Of(product.StockValue.Amount * signedQuantity.Value / product.StockQty.Value)
                    : product.CostPrice * signedQuantity;

                movement = Write(product, StockMovementType.Adjustment, signedQuantity, value, actor,
                    ReferenceAdjustment, null, reason.Trim());
            }
            else
            {
                var quantity = signedQuantity.Abs();
                EnsureAvailable(product, quantity);
                var value = ValueOf(product, quantity);

                movement = Write(product, StockMovementType.Adjustment, signedQuantity, -value, actor,
                    ReferenceAdjustment, null, reason.Trim());
            }

            PostAdjustment(product, movement, reason.Trim(), actor);

            _audit.Record(nameof(Product), productId, AuditAction.Updated, actor,
                new { product.StockQty, product.StockValue },
                new { StockQty = movement.BalanceAfter, StockValue = movement.ValueAfter },
                $"تسوية مخزون: {reason.Trim()}");

            return movement;
        });
    }

    /// <summary>
    /// Sets stock to a counted figure. Returns null when the count already
    /// agrees with the books.
    /// </summary>
    public StockMovement? AdjustToCount(Actor actor, long productId, Quantity counted, string reason)
    {
        if (counted.IsNegative)
        {
            throw new BusinessRuleException("الكمية المعدودة لا يمكن أن تكون سالبة.");
        }

        return _db.InTransaction(() =>
        {
            var product = GetProduct(productId);
            var delta = counted - product.StockQty;
            return delta.IsZero ? null : Adjust(actor, productId, delta, reason);
        });
    }

    /// <summary>
    /// Opening stock when the shop starts using the system: units already on
    /// the shelf, posted Dr Inventory / Cr Opening Balances.
    /// </summary>
    public StockMovement RecordOpeningStock(Actor actor, long productId, Quantity quantity, Money unitCost)
    {
        actor.Require(Permission.InventoryAdjustStock, "ليست لديك صلاحية إدخال أرصدة افتتاحية.");

        if (unitCost.IsNegative)
        {
            throw new BusinessRuleException("تكلفة الوحدة لا يمكن أن تكون سالبة.");
        }

        _accounting.ResolveOpenPeriodFor(_clock.Today);

        return _db.InTransaction(() =>
        {
            var product = GetProduct(productId);
            var value = unitCost * quantity;

            var movement = Receive(productId, quantity, value, StockMovementType.Opening, actor,
                ReferenceAdjustment, null, "رصيد افتتاحي");

            if (value.IsPositive)
            {
                _accounting.Post(
                    $"رصيد افتتاحي للصنف {product.Sku} - {product.Name}",
                    [
                        LedgerLine.Debit(AccountCodes.Inventory, value, "مخزون افتتاحي"),
                        LedgerLine.Credit(AccountCodes.OpeningBalanceEquity, value, "مخزون افتتاحي"),
                    ],
                    JournalSource.Opening,
                    _clock.Today,
                    actor,
                    nameof(StockMovement),
                    movement.Id);
            }

            return movement;
        });
    }

    /// <summary>Movements across all products, newest first, for the stock ledger screen.</summary>
    public IReadOnlyList<StockMovementRow> Movements(DateOnly? from, DateOnly? to, StockMovementType? type = null, string? search = null, long? productId = null) =>
        _db.Query<StockMovementRow>(
            """
            SELECT m.*, p.sku, p.name AS product_name, p.unit, u.full_name AS user_name
            FROM stock_movements m
            JOIN products p ON p.id = m.product_id
            LEFT JOIN users u ON u.id = m.user_id
            WHERE (@from IS NULL OR date(m.created_at) >= @from)
              AND (@to IS NULL OR date(m.created_at) <= @to)
              AND (@type IS NULL OR m.type = @type)
              AND (@productId IS NULL OR m.product_id = @productId)
              AND (@search IS NULL OR p.sku LIKE @like OR p.name LIKE @like OR m.notes LIKE @like)
            ORDER BY m.id DESC
            LIMIT 5000
            """,
            new
            {
                from,
                to,
                type = type?.ToString(),
                productId,
                search = string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
                like = $"%{search?.Trim()}%",
            }).ToList();

    public IReadOnlyList<StockMovement> MovementsFor(long productId) =>
        _db.Query<StockMovement>(
            "SELECT * FROM stock_movements WHERE product_id = @productId ORDER BY id DESC",
            new { productId }).ToList();

    /// <summary>
    /// Products whose cached totals disagree with their movement history. The
    /// triggers make this impossible; the check exists so the integrity screen
    /// can prove it rather than assume it.
    /// </summary>
    public IReadOnlyList<(Product Product, Quantity Actual, Money ActualValue)> FindDrift()
    {
        var rows = _db.Query<(long Id, long Qty, long Value)>(
            """
            SELECT p.id, COALESCE(SUM(m.quantity), 0), COALESCE(SUM(m.value_change), 0)
            FROM products p
            LEFT JOIN stock_movements m ON m.product_id = p.id
            GROUP BY p.id
            HAVING COALESCE(SUM(m.quantity), 0) <> p.stock_qty
                OR COALESCE(SUM(m.value_change), 0) <> p.stock_value
            """).ToList();

        return rows
            .Select(r => (GetProduct(r.Id), Quantity.FromThousandths(r.Qty), Money.FromMinorUnits(r.Value)))
            .ToList();
    }

    /// <summary>The cost value of <paramref name="quantity"/> units taken from current stock.</summary>
    public static Money ValueOf(Product product, Quantity quantity)
    {
        if (quantity >= product.StockQty)
        {
            // The last units take whatever value remains, so nothing is ever
            // left behind as a rounding residue.
            return product.StockValue;
        }

        return Money.Of(product.StockValue.Amount * quantity.Value / product.StockQty.Value);
    }

    private static Money AverageCost(Money value, Quantity quantity) =>
        quantity.IsPositive ? Money.Of(value.Amount / quantity.Value) : Money.Zero;

    private static void EnsureAvailable(Product product, Quantity quantity)
    {
        if (quantity > product.StockQty)
        {
            throw new InsufficientStockException(product.Sku, quantity, product.StockQty);
        }
    }

    private StockMovement Write(
        Product product,
        StockMovementType type,
        Quantity signedQuantity,
        Money signedValue,
        Actor? actor,
        string? referenceType,
        long? referenceId,
        string? notes)
    {
        var balanceAfter = product.StockQty + signedQuantity;
        var valueAfter = product.StockValue + signedValue;
        var unitCost = AverageCost(signedValue.Abs(), signedQuantity.Abs());

        var id = _db.Insert(
            """
            INSERT INTO stock_movements
                (product_id, type, quantity, value_change, balance_after, value_after, unit_cost,
                 reference_type, reference_id, notes, user_id, created_at)
            VALUES
                (@productId, @type, @quantity, @valueChange, @balanceAfter, @valueAfter, @unitCost,
                 @referenceType, @referenceId, @notes, @userId, @now)
            RETURNING id
            """,
            new
            {
                productId = product.Id,
                type = type.ToString(),
                quantity = signedQuantity,
                valueChange = signedValue,
                balanceAfter,
                valueAfter,
                unitCost,
                referenceType,
                referenceId,
                notes,
                userId = actor?.Id,
                now = _clock.Now,
            });

        return _db.QuerySingle<StockMovement>("SELECT * FROM stock_movements WHERE id = @id", new { id });
    }

    private void UpdateCostPrice(long productId, Money costPrice)
    {
        if (costPrice.IsPositive)
        {
            _db.Execute(
                "UPDATE products SET cost_price = @costPrice, updated_at = @now WHERE id = @productId",
                new { productId, costPrice, now = _clock.Now });
        }
    }

    private void PostAdjustment(Product product, StockMovement movement, string reason, Actor actor)
    {
        var value = movement.ValueChange.Abs();

        if (value.IsZero)
        {
            return;
        }

        var memo = $"تسوية مخزون {product.Sku}: {reason}";

        IReadOnlyList<LedgerLine> lines = movement.ValueChange.IsPositive
            ?
            [
                LedgerLine.Debit(AccountCodes.Inventory, value, "زيادة جرد"),
                LedgerLine.Credit(AccountCodes.InventoryAdjustment, value, "زيادة جرد"),
            ]
            :
            [
                LedgerLine.Debit(AccountCodes.InventoryAdjustment, value, "عجز جرد / تالف"),
                LedgerLine.Credit(AccountCodes.Inventory, value, "عجز جرد / تالف"),
            ];

        _accounting.Post(memo, lines, JournalSource.StockAdjustment, _clock.Today, actor,
            nameof(StockMovement), movement.Id);
    }
}

public sealed class StockMovementRow
{
    public long Id { get; set; }
    public long ProductId { get; set; }
    public string Sku { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string Unit { get; set; } = "";
    public StockMovementType Type { get; set; }
    public Quantity Quantity { get; set; }
    public Money ValueChange { get; set; }
    public Quantity BalanceAfter { get; set; }
    public Money ValueAfter { get; set; }
    public Money UnitCost { get; set; }
    public string? ReferenceType { get; set; }
    public long? ReferenceId { get; set; }
    public string? Notes { get; set; }
    public string? UserName { get; set; }
    public DateTime CreatedAt { get; set; }
}
