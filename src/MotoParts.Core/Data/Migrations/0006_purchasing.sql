-- =============================================================================
-- 0006 - Purchasing: suppliers and goods received.
--
-- A purchase is drafted, possibly edited, then received. Receiving is the one
-- step with consequences (stock in, ledger posted) and after it the document is
-- frozen. A draft that is no longer wanted is cancelled, never deleted, so the
-- purchase number series stays gapless.
-- =============================================================================

CREATE TABLE suppliers (
    id                       INTEGER PRIMARY KEY,
    name                     TEXT    NOT NULL,
    contact_person           TEXT,
    phone                    TEXT,
    email                    TEXT,
    address                  TEXT,
    tax_number               TEXT,
    -- Owed to the supplier from before this system, posted to the ledger.
    opening_balance          INTEGER NOT NULL DEFAULT 0,
    opening_journal_entry_id INTEGER REFERENCES journal_entries (id) ON DELETE RESTRICT,
    is_active                INTEGER NOT NULL DEFAULT 1,
    notes                    TEXT,
    created_at               TEXT    NOT NULL DEFAULT (datetime('now', 'localtime')),
    updated_at               TEXT    NOT NULL DEFAULT (datetime('now', 'localtime')),

    CHECK (opening_balance >= 0),
    CHECK (opening_balance = 0 OR opening_journal_entry_id IS NOT NULL),
    CHECK (is_active IN (0, 1)),
    CHECK (length(trim(name)) > 0)
);

CREATE INDEX idx_suppliers_name ON suppliers (name);

CREATE TRIGGER trg_suppliers_opening_balance_frozen
BEFORE UPDATE OF opening_balance, opening_journal_entry_id ON suppliers
WHEN OLD.opening_journal_entry_id IS NOT NULL
 AND (NEW.opening_balance <> OLD.opening_balance
   OR NEW.opening_journal_entry_id IS NOT OLD.opening_journal_entry_id)
BEGIN
    SELECT RAISE(ABORT, 'الرصيد الافتتاحي للمورد مسجل في الدفاتر ولا يمكن تعديله.');
END;

CREATE TABLE purchases (
    id                      INTEGER PRIMARY KEY,
    purchase_number         TEXT    NOT NULL UNIQUE,
    supplier_id             INTEGER NOT NULL REFERENCES suppliers (id) ON DELETE RESTRICT,
    user_id                 INTEGER NOT NULL REFERENCES users (id) ON DELETE RESTRICT,
    purchase_date           TEXT    NOT NULL,
    supplier_invoice_number TEXT,
    -- Basis points: 1500 = 15%. Purchase costs are always tax-exclusive, as
    -- supplier invoices show tax separately.
    tax_rate_bp             INTEGER NOT NULL DEFAULT 0,
    subtotal                INTEGER NOT NULL DEFAULT 0,
    tax_total               INTEGER NOT NULL DEFAULT 0,
    total                   INTEGER NOT NULL DEFAULT 0,
    status                  TEXT    NOT NULL DEFAULT 'Draft',
    -- Credit raises a payable; anything else settles now.
    payment_method          TEXT    NOT NULL DEFAULT 'Credit',
    received_at             TEXT,
    received_by             INTEGER REFERENCES users (id) ON DELETE SET NULL,
    cancelled_at            TEXT,
    cancelled_by            INTEGER REFERENCES users (id) ON DELETE SET NULL,
    cancel_reason           TEXT,
    journal_entry_id        INTEGER REFERENCES journal_entries (id) ON DELETE RESTRICT,
    notes                   TEXT,
    created_at              TEXT    NOT NULL DEFAULT (datetime('now', 'localtime')),
    updated_at              TEXT    NOT NULL DEFAULT (datetime('now', 'localtime')),

    CHECK (tax_rate_bp >= 0 AND tax_rate_bp <= 10000),
    CHECK (subtotal >= 0 AND tax_total >= 0),
    CHECK (total = subtotal + tax_total),
    CHECK (status IN ('Draft', 'Received', 'Cancelled')),
    CHECK (payment_method IN ('Cash', 'Card', 'Transfer', 'Credit')),
    CHECK (status <> 'Received' OR (received_at IS NOT NULL AND journal_entry_id IS NOT NULL)),
    CHECK (status <> 'Cancelled' OR (cancelled_at IS NOT NULL AND length(trim(cancel_reason)) > 0))
);

CREATE INDEX idx_purchases_date ON purchases (purchase_date, status);
CREATE INDEX idx_purchases_supplier ON purchases (supplier_id, status);

-- Received and cancelled are final.
CREATE TRIGGER trg_purchases_final_states
BEFORE UPDATE ON purchases
WHEN OLD.status <> 'Draft'
BEGIN
    SELECT RAISE(ABORT, 'أمر الشراء مستلم أو ملغى ولا يمكن تعديله.');
END;

CREATE TRIGGER trg_purchases_no_delete
BEFORE DELETE ON purchases
BEGIN
    SELECT RAISE(ABORT, 'لا يمكن حذف أمر شراء؛ ألغِ المسودة بدلاً من ذلك.');
END;

CREATE TABLE purchase_items (
    id          INTEGER PRIMARY KEY,
    purchase_id INTEGER NOT NULL REFERENCES purchases (id) ON DELETE CASCADE,
    product_id  INTEGER NOT NULL REFERENCES products (id) ON DELETE RESTRICT,
    sku         TEXT    NOT NULL,
    name        TEXT    NOT NULL,
    quantity    INTEGER NOT NULL,
    unit_cost   INTEGER NOT NULL,
    line_total  INTEGER NOT NULL,

    CHECK (quantity > 0),
    CHECK (unit_cost >= 0 AND line_total >= 0)
);

CREATE INDEX idx_purchase_items_purchase ON purchase_items (purchase_id);
CREATE INDEX idx_purchase_items_product ON purchase_items (product_id);

CREATE TRIGGER trg_purchase_items_insert_draft_only
BEFORE INSERT ON purchase_items
WHEN (SELECT status FROM purchases WHERE id = NEW.purchase_id) <> 'Draft'
BEGIN
    SELECT RAISE(ABORT, 'لا يمكن تعديل أسطر أمر شراء غير المسودة.');
END;

CREATE TRIGGER trg_purchase_items_update_draft_only
BEFORE UPDATE ON purchase_items
WHEN (SELECT status FROM purchases WHERE id = OLD.purchase_id) <> 'Draft'
BEGIN
    SELECT RAISE(ABORT, 'لا يمكن تعديل أسطر أمر شراء غير المسودة.');
END;

CREATE TRIGGER trg_purchase_items_delete_draft_only
BEFORE DELETE ON purchase_items
WHEN (SELECT status FROM purchases WHERE id = OLD.purchase_id) <> 'Draft'
BEGIN
    SELECT RAISE(ABORT, 'لا يمكن تعديل أسطر أمر شراء غير المسودة.');
END;
