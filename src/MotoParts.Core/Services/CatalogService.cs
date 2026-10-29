using Dapper;
using MotoParts.Core.Common;
using MotoParts.Core.Data;
using MotoParts.Core.Domain;
using MotoParts.Core.Security;

namespace MotoParts.Core.Services;

/// <summary>
/// Categories and products: the catalogue, and the till's search.
///
/// Stock and cost are not editable here - they move only through stock
/// movements (receipts, sales, adjustments), so the product form cannot be
/// used to quietly change what the shelf is worth.
/// </summary>
public sealed class CatalogService
{
    private readonly DbSession _db;
    private readonly IClock _clock;
    private readonly AuditService _audit;

    public CatalogService(DbSession db, IClock clock, AuditService audit)
    {
        _db = db;
        _clock = clock;
        _audit = audit;
    }

    // --- Categories -----------------------------------------------------------

    public IReadOnlyList<CategoryRow> Categories(bool activeOnly = false) =>
        _db.Query<CategoryRow>(
            """
            SELECT c.id, c.name, c.description, c.is_active,
                   (SELECT COUNT(*) FROM products p WHERE p.category_id = c.id) AS product_count
            FROM categories c
            WHERE (@activeOnly = 0 OR c.is_active = 1)
            ORDER BY c.name
            """,
            new { activeOnly }).ToList();

    public Category SaveCategory(long? id, string name, string? description, bool isActive, Actor actor)
    {
        actor.Require(Permission.InventoryManageProducts, "ليست لديك صلاحية إدارة الأصناف.");

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new BusinessRuleException("اسم الفئة مطلوب.");
        }

        return _db.InTransaction(() =>
        {
            var duplicate = _db.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM categories WHERE name = @name COLLATE NOCASE AND id IS NOT @id",
                new { name = name.Trim(), id }) > 0;

            if (duplicate)
            {
                throw new BusinessRuleException($"توجد فئة بالاسم \"{name.Trim()}\" من قبل.");
            }

            var categoryId = id ?? _db.Insert(
                "INSERT INTO categories (name, description, is_active) VALUES (@name, @description, @isActive) RETURNING id",
                new { name = name.Trim(), description = Clean(description), isActive });

            if (id is not null)
            {
                _db.Execute(
                    "UPDATE categories SET name = @name, description = @description, is_active = @isActive, updated_at = @now WHERE id = @id",
                    new { id, name = name.Trim(), description = Clean(description), isActive, now = _clock.Now });
            }

            return _db.QuerySingle<Category>("SELECT * FROM categories WHERE id = @categoryId", new { categoryId });
        });
    }

    public void DeleteCategory(long id, Actor actor)
    {
        actor.Require(Permission.InventoryManageProducts, "ليست لديك صلاحية إدارة الأصناف.");

        var used = _db.ExecuteScalar<long>("SELECT COUNT(*) FROM products WHERE category_id = @id", new { id });

        if (used > 0)
        {
            throw new BusinessRuleException($"الفئة مستخدمة في {used} صنف؛ أوقفها بدلاً من حذفها.");
        }

        _db.InTransaction(() => _db.Execute("DELETE FROM categories WHERE id = @id", new { id }));
    }

    // --- Products -------------------------------------------------------------

    public Product Get(long id) =>
        _db.QueryFirstOrDefault<Product>("SELECT * FROM products WHERE id = @id", new { id })
        ?? throw new BusinessRuleException("الصنف غير موجود.");

    /// <summary>Product list with filters, for the products screen.</summary>
    public IReadOnlyList<ProductRow> Products(string? search = null, long? categoryId = null, bool activeOnly = false, bool lowStockOnly = false) =>
        _db.Query<ProductRow>(
            """
            SELECT p.*, c.name AS category_name
            FROM products p
            LEFT JOIN categories c ON c.id = p.category_id
            WHERE (@search IS NULL
                   OR p.sku LIKE @like OR p.barcode = @search OR p.name LIKE @like
                   OR p.brand LIKE @like OR p.fits_models LIKE @like)
              AND (@categoryId IS NULL OR p.category_id = @categoryId)
              AND (@activeOnly = 0 OR p.is_active = 1)
              AND (@lowStockOnly = 0 OR (p.is_active = 1 AND p.reorder_level > 0 AND p.stock_qty <= p.reorder_level))
            ORDER BY p.name
            LIMIT 2000
            """,
            new
            {
                search = Clean(search),
                like = $"%{search?.Trim()}%",
                categoryId,
                activeOnly,
                lowStockOnly,
            }).ToList();

    /// <summary>
    /// The till's fast path: an exact SKU or barcode, as a scanner sends it.
    /// </summary>
    public Product? FindByCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        return _db.QueryFirstOrDefault<Product>(
            "SELECT * FROM products WHERE is_active = 1 AND (sku = @code COLLATE NOCASE OR barcode = @code) LIMIT 1",
            new { code = code.Trim() });
    }

    /// <summary>The till's fallback: SKU prefix, name, brand or fitment.</summary>
    public IReadOnlyList<Product> SearchForSale(string term, int limit = 30)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return [];
        }

        return _db.Query<Product>(
            """
            SELECT * FROM products
            WHERE is_active = 1
              AND (sku LIKE @prefix OR name LIKE @like OR brand LIKE @like OR fits_models LIKE @like)
            ORDER BY CASE WHEN sku LIKE @prefix THEN 0 ELSE 1 END, name
            LIMIT @limit
            """,
            new { prefix = $"{term.Trim()}%", like = $"%{term.Trim()}%", limit }).ToList();
    }

    public Product CreateProduct(ProductInput input, Actor actor)
    {
        actor.Require(Permission.InventoryManageProducts, "ليست لديك صلاحية إدارة الأصناف.");
        Validate(input);

        return _db.InTransaction(() =>
        {
            EnsureUnique(input, null);

            var id = _db.Insert(
                """
                INSERT INTO products
                    (sku, barcode, name, description, category_id, brand, fits_models, unit,
                     selling_price, cost_price, reorder_level, is_active, created_at, updated_at)
                VALUES
                    (@Sku, @Barcode, @Name, @Description, @CategoryId, @Brand, @FitsModels, @Unit,
                     @SellingPrice, @CostPrice, @ReorderLevel, @IsActive, @now, @now)
                RETURNING id
                """,
                Parameters(input));

            _audit.Record(nameof(Product), id, AuditAction.Created, actor, newValues: input);

            return Get(id);
        });
    }

    public Product UpdateProduct(long id, ProductInput input, Actor actor)
    {
        actor.Require(Permission.InventoryManageProducts, "ليست لديك صلاحية إدارة الأصناف.");
        Validate(input);

        return _db.InTransaction(() =>
        {
            var before = Get(id);
            EnsureUnique(input, id);

            // cost_price is not taken from the form once stock exists: it is
            // the average of what was actually paid. With nothing ever received,
            // the form's figure is the expected cost for valuing found stock.
            var hasHistory = _db.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM stock_movements WHERE product_id = @id", new { id }) > 0;

            var parameters = Parameters(input with { CostPrice = hasHistory ? before.CostPrice : input.CostPrice });

            _db.Execute(
                """
                UPDATE products SET
                    sku = @Sku, barcode = @Barcode, name = @Name, description = @Description,
                    category_id = @CategoryId, brand = @Brand, fits_models = @FitsModels, unit = @Unit,
                    selling_price = @SellingPrice, cost_price = @CostPrice, reorder_level = @ReorderLevel,
                    is_active = @IsActive, updated_at = @now
                WHERE id = @id
                """,
                WithId(parameters, id));

            _audit.Record(nameof(Product), id, AuditAction.Updated, actor,
                new { before.Sku, before.Name, before.SellingPrice, before.ReorderLevel, before.IsActive },
                new { input.Sku, input.Name, input.SellingPrice, input.ReorderLevel, input.IsActive });

            return Get(id);
        });
    }

    /// <summary>Deletes a product that has never been used; anything else is deactivated instead.</summary>
    public void DeleteProduct(long id, Actor actor)
    {
        actor.Require(Permission.InventoryManageProducts, "ليست لديك صلاحية إدارة الأصناف.");

        var used = _db.ExecuteScalar<long>(
            """
            SELECT (SELECT COUNT(*) FROM stock_movements WHERE product_id = @id)
                 + (SELECT COUNT(*) FROM sale_items WHERE product_id = @id)
                 + (SELECT COUNT(*) FROM purchase_items WHERE product_id = @id)
            """,
            new { id });

        if (used > 0)
        {
            throw new BusinessRuleException("للصنف حركات سابقة ولا يمكن حذفه؛ أوقفه بدلاً من ذلك.");
        }

        _db.InTransaction(() =>
        {
            var before = Get(id);
            _db.Execute("DELETE FROM products WHERE id = @id", new { id });
            _audit.Record(nameof(Product), id, AuditAction.Deleted, actor, new { before.Sku, before.Name });
        });
    }

    private void EnsureUnique(ProductInput input, long? id)
    {
        if (_db.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM products WHERE sku = @sku COLLATE NOCASE AND id IS NOT @id",
                new { sku = input.Sku.Trim(), id }) > 0)
        {
            throw new BusinessRuleException($"رمز الصنف {input.Sku.Trim()} مستخدم لصنف آخر.");
        }

        if (!string.IsNullOrWhiteSpace(input.Barcode)
            && _db.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM products WHERE barcode = @barcode AND id IS NOT @id",
                new { barcode = input.Barcode.Trim(), id }) > 0)
        {
            throw new BusinessRuleException($"الباركود {input.Barcode.Trim()} مستخدم لصنف آخر.");
        }
    }

    private static void Validate(ProductInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Sku))
        {
            throw new BusinessRuleException("رمز الصنف مطلوب.");
        }

        if (string.IsNullOrWhiteSpace(input.Name))
        {
            throw new BusinessRuleException("اسم الصنف مطلوب.");
        }

        if (string.IsNullOrWhiteSpace(input.Unit))
        {
            throw new BusinessRuleException("وحدة الصنف مطلوبة.");
        }

        if (input.SellingPrice.IsNegative || input.CostPrice.IsNegative)
        {
            throw new BusinessRuleException("الأسعار لا يمكن أن تكون سالبة.");
        }

        if (input.ReorderLevel.IsNegative)
        {
            throw new BusinessRuleException("حد إعادة الطلب لا يمكن أن يكون سالباً.");
        }
    }

    private object Parameters(ProductInput input) => new
    {
        Sku = input.Sku.Trim(),
        Barcode = Clean(input.Barcode),
        Name = input.Name.Trim(),
        Description = Clean(input.Description),
        input.CategoryId,
        Brand = Clean(input.Brand),
        FitsModels = Clean(input.FitsModels),
        Unit = input.Unit.Trim(),
        input.SellingPrice,
        input.CostPrice,
        input.ReorderLevel,
        input.IsActive,
        now = _clock.Now,
    };

    private static DynamicParameters WithId(object parameters, long id)
    {
        var combined = new DynamicParameters(parameters);
        combined.Add("id", id);
        return combined;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record ProductInput(
    string Sku,
    string? Barcode,
    string Name,
    string? Description,
    long? CategoryId,
    string? Brand,
    string? FitsModels,
    string Unit,
    Money SellingPrice,
    Money CostPrice,
    Quantity ReorderLevel,
    bool IsActive);

public sealed class CategoryRow
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool IsActive { get; set; }
    public long ProductCount { get; set; }
}

public sealed class ProductRow
{
    public long Id { get; set; }
    public string Sku { get; set; } = "";
    public string? Barcode { get; set; }
    public string Name { get; set; } = "";
    public long? CategoryId { get; set; }
    public string? CategoryName { get; set; }
    public string? Brand { get; set; }
    public string? FitsModels { get; set; }
    public string Unit { get; set; } = "";
    public Money SellingPrice { get; set; }
    public Money CostPrice { get; set; }
    public Quantity StockQty { get; set; }
    public Money StockValue { get; set; }
    public Quantity ReorderLevel { get; set; }
    public bool IsActive { get; set; }

    public bool IsLowStock => IsActive && ReorderLevel.IsPositive && StockQty <= ReorderLevel;
}
