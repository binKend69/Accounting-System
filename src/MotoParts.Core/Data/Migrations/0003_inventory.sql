-- =============================================================================
-- 0003 - Inventory: categories, products, stock movements.
--
-- stock_movements is the source of truth for stock. Each product carries a
-- cached quantity AND a cached value (cost of the units on hand). Both are
-- maintained by a trigger on stock_movements - never written by the services -
-- and each movement must continue the chain exactly from the previous one.
-- The result: the cache cannot drift, and the Inventory account can be proved
-- equal to the sum of stock values to the halala.
-- =============================================================================

CREATE TABLE categories (
    id          INTEGER PRIMARY KEY,
    name        TEXT    NOT NULL UNIQUE COLLATE NOCASE,
    description TEXT,
    is_active   INTEGER NOT NULL DEFAULT 1,
    created_at  TEXT    NOT NULL DEFAULT (datetime('now', 'localtime')),
    updated_at  TEXT    NOT NULL DEFAULT (datetime('now', 'localtime')),

    CHECK (is_active IN (0, 1)),
    CHECK (length(trim(name)) > 0)
);

CREATE TABLE products (
    id            INTEGER PRIMARY KEY,
    sku           TEXT    NOT NULL UNIQUE COLLATE NOCASE,
    -- Scanned at the till. Optional (many small parts arrive without one), but
    -- unique when present.
    barcode       TEXT    UNIQUE,
    name          TEXT    NOT NULL,
    description   TEXT,
    category_id   INTEGER REFERENCES categories (id) ON DELETE SET NULL,
    brand         TEXT,
    -- Fitment note, e.g. "Honda CG125 2004-2012": what counter staff search by.
    fits_models   TEXT,
    unit          TEXT    NOT NULL DEFAULT 'قطعة',
    selling_price INTEGER NOT NULL DEFAULT 0,
    -- Average cost per unit, for display and as the fallback cost when stock is
    -- added back at zero on hand. The value that drives the books is stock_value.
    cost_price    INTEGER NOT NULL DEFAULT 0,
    -- Cached from stock_movements by trigger. Never written directly.
    stock_qty     INTEGER NOT NULL DEFAULT 0,
    stock_value   INTEGER NOT NULL DEFAULT 0,
    reorder_level INTEGER NOT NULL DEFAULT 0,
    is_active     INTEGER NOT NULL DEFAULT 1,
    created_at    TEXT    NOT NULL DEFAULT (datetime('now', 'localtime')),
    updated_at    TEXT    NOT NULL DEFAULT (datetime('now', 'localtime')),

    CHECK (selling_price >= 0),
    CHECK (cost_price >= 0),
    CHECK (stock_qty >= 0),
    CHECK (stock_value >= 0),
    -- Nothing on the shelf means nothing on the books.
    CHECK (stock_qty > 0 OR stock_value = 0),
    CHECK (reorder_level >= 0),
    CHECK (is_active IN (0, 1)),
    CHECK (length(trim(sku)) > 0),
    CHECK (length(trim(name)) > 0),
    CHECK (barcode IS NULL OR length(trim(barcode)) > 0)
);

CREATE INDEX idx_products_name ON products (is_active, name);
CREATE INDEX idx_products_category ON products (category_id);
CREATE INDEX idx_products_low_stock ON products (stock_qty, reorder_level);

-- Stock arrives through movements only, so a product starts empty.
CREATE TRIGGER trg_products_start_empty
BEFORE INSERT ON products
WHEN NEW.stock_qty <> 0 OR NEW.stock_value <> 0
BEGIN
    SELECT RAISE(ABORT, 'الصنف الجديد يبدأ برصيد صفر؛ أدخل الرصيد الافتتاحي كحركة مخزون.');
END;

-- The cached figures may only ever equal the latest movement's running totals.
CREATE TRIGGER trg_products_stock_follows_movements
BEFORE UPDATE OF stock_qty, stock_value ON products
WHEN (NEW.stock_qty <> OLD.stock_qty OR NEW.stock_value <> OLD.stock_value)
 AND NOT EXISTS (
    SELECT 1 FROM stock_movements m
    WHERE m.id = (SELECT MAX(id) FROM stock_movements WHERE product_id = NEW.id)
      AND m.balance_after = NEW.stock_qty
      AND m.value_after = NEW.stock_value
 )
BEGIN
    SELECT RAISE(ABORT, 'رصيد الصنف يُحدَّث من حركات المخزون فقط ولا يُعدَّل مباشرة.');
END;

-- -----------------------------------------------------------------------------
-- Stock movements: an append-only ledger of every unit in and out.
-- -----------------------------------------------------------------------------

CREATE TABLE stock_movements (
    id             INTEGER PRIMARY KEY,
    product_id     INTEGER NOT NULL REFERENCES products (id) ON DELETE RESTRICT,
    type           TEXT    NOT NULL,
    -- Signed thousandths: positive in, negative out. The product balance is a
    -- plain SUM over this column.
    quantity       INTEGER NOT NULL,
    -- Signed cost value moved, in minor units. Inbound: what the units cost.
    -- Outbound: the share of stock value removed - the COGS of a sale.
    value_change   INTEGER NOT NULL,
    -- Running totals immediately after this movement.
    balance_after  INTEGER NOT NULL,
    value_after    INTEGER NOT NULL,
    -- Unit cost at the moment of the movement, for display.
    unit_cost      INTEGER NOT NULL DEFAULT 0,
    reference_type TEXT,
    reference_id   INTEGER,
    notes          TEXT,
    user_id        INTEGER REFERENCES users (id) ON DELETE SET NULL,
    created_at     TEXT    NOT NULL,

    CHECK (type IN ('Opening', 'Purchase', 'Sale', 'SaleReturn', 'PurchaseReturn', 'Adjustment')),
    CHECK (quantity <> 0),
    -- Never below zero: the last line of defence behind the service's check.
    CHECK (balance_after >= 0),
    CHECK (value_after >= 0),
    CHECK (balance_after > 0 OR value_after = 0),
    CHECK (unit_cost >= 0),
    -- Value moves with the units: in adds, out removes.
    CHECK ((quantity > 0 AND value_change >= 0) OR (quantity < 0 AND value_change <= 0)),
    -- A receipt that removes stock, or a sale that adds it, would corrupt both
    -- stock and cost of sales. Only an adjustment may point either way.
    CHECK (
        (type IN ('Opening', 'Purchase', 'SaleReturn') AND quantity > 0)
        OR (type IN ('Sale', 'PurchaseReturn') AND quantity < 0)
        OR type = 'Adjustment'
    )
);

CREATE INDEX idx_stock_movements_product ON stock_movements (product_id, id);
CREATE INDEX idx_stock_movements_type ON stock_movements (type, created_at);
CREATE INDEX idx_stock_movements_reference ON stock_movements (reference_type, reference_id);

-- Each movement must continue exactly from the product's current totals.
CREATE TRIGGER trg_stock_movements_chain
BEFORE INSERT ON stock_movements
BEGIN
    SELECT RAISE(ABORT, 'حركة المخزون لا تتسلسل من الرصيد الحالي للصنف.')
    WHERE NEW.balance_after <> (SELECT stock_qty FROM products WHERE id = NEW.product_id) + NEW.quantity
       OR NEW.value_after <> (SELECT stock_value FROM products WHERE id = NEW.product_id) + NEW.value_change;
END;

CREATE TRIGGER trg_stock_movements_update_cache
AFTER INSERT ON stock_movements
BEGIN
    UPDATE products
    SET stock_qty = NEW.balance_after,
        stock_value = NEW.value_after
    WHERE id = NEW.product_id;
END;

CREATE TRIGGER trg_stock_movements_no_update
BEFORE UPDATE ON stock_movements
BEGIN
    SELECT RAISE(ABORT, 'حركات المخزون لا تُعدَّل. صحّح الخطأ بحركة تسوية جديدة.');
END;

CREATE TRIGGER trg_stock_movements_no_delete
BEFORE DELETE ON stock_movements
BEGIN
    SELECT RAISE(ABORT, 'حركات المخزون لا تُحذف. صحّح الخطأ بحركة تسوية جديدة.');
END;
