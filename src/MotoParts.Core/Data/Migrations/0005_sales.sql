-- =============================================================================
-- 0005 - Sales: customers, invoices, returns.
--
-- An invoice is never edited or deleted. A mistake is corrected with a refund:
-- its own numbered document, with its own journal entry and stock movements.
-- The triggers below make that true at the database level - the only columns
-- of a sale that can ever change are the running refund totals and its status.
--
-- Price basis: when prices_include_tax = 1, unit prices and line totals include
-- tax and the tax is carved out of them; otherwise tax is added on top. Either
-- way: total = subtotal - discount_total (+ tax_total when exclusive).
-- =============================================================================

CREATE TABLE customers (
    id                       INTEGER PRIMARY KEY,
    name                     TEXT    NOT NULL,
    phone                    TEXT,
    email                    TEXT,
    address                  TEXT,
    tax_number               TEXT,
    -- Zero means cash only; credit sales are refused.
    credit_limit             INTEGER NOT NULL DEFAULT 0,
    -- Debt brought forward from before this system, posted to the ledger.
    opening_balance          INTEGER NOT NULL DEFAULT 0,
    opening_journal_entry_id INTEGER REFERENCES journal_entries (id) ON DELETE RESTRICT,
    is_active                INTEGER NOT NULL DEFAULT 1,
    notes                    TEXT,
    created_at               TEXT    NOT NULL DEFAULT (datetime('now', 'localtime')),
    updated_at               TEXT    NOT NULL DEFAULT (datetime('now', 'localtime')),

    CHECK (credit_limit >= 0),
    CHECK (opening_balance >= 0),
    CHECK (opening_balance = 0 OR opening_journal_entry_id IS NOT NULL),
    CHECK (is_active IN (0, 1)),
    CHECK (length(trim(name)) > 0)
);

CREATE INDEX idx_customers_name ON customers (name);
CREATE INDEX idx_customers_phone ON customers (phone);

-- The opening balance is in the ledger; changing it here would orphan it.
CREATE TRIGGER trg_customers_opening_balance_frozen
BEFORE UPDATE OF opening_balance, opening_journal_entry_id ON customers
WHEN OLD.opening_journal_entry_id IS NOT NULL
 AND (NEW.opening_balance <> OLD.opening_balance
   OR NEW.opening_journal_entry_id IS NOT OLD.opening_journal_entry_id)
BEGIN
    SELECT RAISE(ABORT, 'الرصيد الافتتاحي للعميل مسجل في الدفاتر ولا يمكن تعديله.');
END;

-- -----------------------------------------------------------------------------
-- Sales (invoices)
-- -----------------------------------------------------------------------------

CREATE TABLE sales (
    id                 INTEGER PRIMARY KEY,
    -- Gapless per year, allocated inside the checkout transaction.
    invoice_number     TEXT    NOT NULL UNIQUE,
    sale_date          TEXT    NOT NULL,
    sold_at            TEXT    NOT NULL,
    customer_id        INTEGER REFERENCES customers (id) ON DELETE RESTRICT,
    user_id            INTEGER NOT NULL REFERENCES users (id) ON DELETE RESTRICT,
    -- Mandatory: a sale only exists inside an open till session.
    shift_session_id   INTEGER NOT NULL REFERENCES shift_sessions (id) ON DELETE RESTRICT,

    prices_include_tax INTEGER NOT NULL,
    -- Basis points: 1500 = 15%.
    tax_rate_bp        INTEGER NOT NULL,
    subtotal           INTEGER NOT NULL,
    discount_total     INTEGER NOT NULL DEFAULT 0,
    tax_total          INTEGER NOT NULL DEFAULT 0,
    total              INTEGER NOT NULL,
    -- Cost value of the goods at the moment of sale, so margin never depends on
    -- today's cost.
    cogs_total         INTEGER NOT NULL DEFAULT 0,

    payment_method     TEXT    NOT NULL,
    amount_paid        INTEGER NOT NULL DEFAULT 0,
    change_due         INTEGER NOT NULL DEFAULT 0,

    status             TEXT    NOT NULL DEFAULT 'Completed',
    -- Running totals of returns against this invoice (price basis and tax), so
    -- several partial refunds can never add up to more than was sold.
    refunded_total     INTEGER NOT NULL DEFAULT 0,
    refunded_tax       INTEGER NOT NULL DEFAULT 0,

    journal_entry_id   INTEGER REFERENCES journal_entries (id) ON DELETE RESTRICT,
    notes              TEXT,
    created_at         TEXT    NOT NULL DEFAULT (datetime('now', 'localtime')),

    CHECK (prices_include_tax IN (0, 1)),
    CHECK (tax_rate_bp >= 0 AND tax_rate_bp <= 10000),
    CHECK (subtotal >= 0 AND discount_total >= 0 AND tax_total >= 0 AND total >= 0 AND cogs_total >= 0),
    CHECK (discount_total <= subtotal),
    CHECK (total = subtotal - discount_total + CASE WHEN prices_include_tax = 1 THEN 0 ELSE tax_total END),
    CHECK (payment_method IN ('Cash', 'Card', 'Transfer', 'Credit')),
    CHECK (payment_method <> 'Credit' OR customer_id IS NOT NULL),
    -- How each method settles.
    CHECK (
        (payment_method = 'Cash' AND amount_paid >= total AND change_due = amount_paid - total)
        OR (payment_method IN ('Card', 'Transfer') AND amount_paid = total AND change_due = 0)
        OR (payment_method = 'Credit' AND amount_paid = 0 AND change_due = 0)
    ),
    CHECK (status IN ('Completed', 'PartiallyRefunded', 'Refunded')),
    CHECK (refunded_total >= 0 AND refunded_total <= total),
    CHECK (refunded_tax >= 0 AND refunded_tax <= tax_total),
    CHECK (status <> 'Completed' OR (refunded_total = 0 AND refunded_tax = 0)),
    CHECK (status <> 'Refunded' OR (refunded_total = total AND refunded_tax = tax_total))
);

CREATE INDEX idx_sales_date ON sales (sale_date, status);
CREATE INDEX idx_sales_session ON sales (shift_session_id, payment_method);
CREATE INDEX idx_sales_user ON sales (user_id, sale_date);
CREATE INDEX idx_sales_customer ON sales (customer_id, payment_method);

-- The main guardrail: no sale outside the seller's own open till.
CREATE TRIGGER trg_sales_require_open_session
BEFORE INSERT ON sales
WHEN NOT EXISTS (
    SELECT 1 FROM shift_sessions
    WHERE id = NEW.shift_session_id AND status = 'Open' AND user_id = NEW.user_id
)
BEGIN
    SELECT RAISE(ABORT, 'لا يمكن تسجيل بيع خارج وردية مفتوحة باسم البائع.');
END;

CREATE TRIGGER trg_sales_immutable
BEFORE UPDATE ON sales
WHEN NEW.invoice_number <> OLD.invoice_number
  OR NEW.sale_date <> OLD.sale_date
  OR NEW.sold_at <> OLD.sold_at
  OR NEW.customer_id IS NOT OLD.customer_id
  OR NEW.user_id <> OLD.user_id
  OR NEW.shift_session_id <> OLD.shift_session_id
  OR NEW.prices_include_tax <> OLD.prices_include_tax
  OR NEW.tax_rate_bp <> OLD.tax_rate_bp
  OR NEW.subtotal <> OLD.subtotal
  OR NEW.discount_total <> OLD.discount_total
  OR NEW.tax_total <> OLD.tax_total
  OR NEW.total <> OLD.total
  OR NEW.cogs_total <> OLD.cogs_total
  OR NEW.payment_method <> OLD.payment_method
  OR NEW.amount_paid <> OLD.amount_paid
  OR NEW.change_due <> OLD.change_due
  OR NEW.notes IS NOT OLD.notes
  OR (OLD.journal_entry_id IS NOT NULL AND NEW.journal_entry_id IS NOT OLD.journal_entry_id)
  OR NEW.refunded_total < OLD.refunded_total
  OR NEW.refunded_tax < OLD.refunded_tax
BEGIN
    SELECT RAISE(ABORT, 'الفاتورة لا تُعدَّل بعد إصدارها. صحّح الخطأ بعملية استرجاع.');
END;

CREATE TRIGGER trg_sales_no_delete
BEFORE DELETE ON sales
BEGIN
    SELECT RAISE(ABORT, 'لا يمكن حذف فاتورة. صحّح الخطأ بعملية استرجاع.');
END;

-- -----------------------------------------------------------------------------
-- Sale lines
-- -----------------------------------------------------------------------------

CREATE TABLE sale_items (
    id                INTEGER PRIMARY KEY,
    sale_id           INTEGER NOT NULL REFERENCES sales (id) ON DELETE RESTRICT,
    product_id        INTEGER NOT NULL REFERENCES products (id) ON DELETE RESTRICT,
    -- Copied at the time of sale so the invoice still reads correctly after
    -- the product is renamed or re-priced.
    sku               TEXT    NOT NULL,
    name              TEXT    NOT NULL,
    unit              TEXT    NOT NULL,
    quantity          INTEGER NOT NULL,
    unit_price        INTEGER NOT NULL,
    discount          INTEGER NOT NULL DEFAULT 0,
    -- quantity x unit_price - discount, in the sale's price basis.
    line_total        INTEGER NOT NULL,
    unit_cost         INTEGER NOT NULL,
    line_cogs         INTEGER NOT NULL,
    -- Returned so far. Each refund of the final remainder takes exactly what is
    -- left, so a line refunded in parts always sums back to the original.
    refunded_quantity INTEGER NOT NULL DEFAULT 0,
    refunded_amount   INTEGER NOT NULL DEFAULT 0,
    refunded_cogs     INTEGER NOT NULL DEFAULT 0,

    CHECK (quantity > 0),
    CHECK (unit_price >= 0 AND discount >= 0 AND line_total >= 0),
    CHECK (unit_cost >= 0 AND line_cogs >= 0),
    CHECK (refunded_quantity >= 0 AND refunded_quantity <= quantity),
    CHECK (refunded_amount >= 0 AND refunded_amount <= line_total),
    CHECK (refunded_cogs >= 0 AND refunded_cogs <= line_cogs),
    CHECK (refunded_quantity < quantity OR (refunded_amount = line_total AND refunded_cogs = line_cogs))
);

CREATE INDEX idx_sale_items_sale ON sale_items (sale_id);
CREATE INDEX idx_sale_items_product ON sale_items (product_id);

CREATE TRIGGER trg_sale_items_only_while_open
BEFORE INSERT ON sale_items
WHEN (SELECT journal_entry_id FROM sales WHERE id = NEW.sale_id) IS NOT NULL
BEGIN
    SELECT RAISE(ABORT, 'لا يمكن إضافة سطر إلى فاتورة صادرة.');
END;

CREATE TRIGGER trg_sale_items_immutable
BEFORE UPDATE ON sale_items
WHEN NEW.sale_id <> OLD.sale_id
  OR NEW.product_id <> OLD.product_id
  OR NEW.sku <> OLD.sku
  OR NEW.name <> OLD.name
  OR NEW.unit <> OLD.unit
  OR NEW.quantity <> OLD.quantity
  OR NEW.unit_price <> OLD.unit_price
  OR NEW.discount <> OLD.discount
  OR NEW.line_total <> OLD.line_total
  OR NEW.unit_cost <> OLD.unit_cost
  OR NEW.line_cogs <> OLD.line_cogs
  OR NEW.refunded_quantity < OLD.refunded_quantity
  OR NEW.refunded_amount < OLD.refunded_amount
  OR NEW.refunded_cogs < OLD.refunded_cogs
BEGIN
    SELECT RAISE(ABORT, 'سطر الفاتورة لا يُعدَّل بعد إصدارها.');
END;

CREATE TRIGGER trg_sale_items_no_delete
BEFORE DELETE ON sale_items
BEGIN
    SELECT RAISE(ABORT, 'لا يمكن حذف سطر من فاتورة.');
END;

-- -----------------------------------------------------------------------------
-- Refunds
-- -----------------------------------------------------------------------------

CREATE TABLE sale_refunds (
    id                 INTEGER PRIMARY KEY,
    refund_number      TEXT    NOT NULL UNIQUE,
    sale_id            INTEGER NOT NULL REFERENCES sales (id) ON DELETE RESTRICT,
    refund_date        TEXT    NOT NULL,
    refunded_at        TEXT    NOT NULL,
    user_id            INTEGER NOT NULL REFERENCES users (id) ON DELETE RESTRICT,
    -- Money leaves a drawer, so a refund belongs to a till session too.
    shift_session_id   INTEGER NOT NULL REFERENCES shift_sessions (id) ON DELETE RESTRICT,
    prices_include_tax INTEGER NOT NULL,
    subtotal           INTEGER NOT NULL,
    tax_total          INTEGER NOT NULL DEFAULT 0,
    total              INTEGER NOT NULL,
    -- Cost value of the returned units, and the part of it put back on the shelf.
    cogs_total         INTEGER NOT NULL DEFAULT 0,
    restocked_cogs     INTEGER NOT NULL DEFAULT 0,
    payment_method     TEXT    NOT NULL,
    reason             TEXT    NOT NULL,
    journal_entry_id   INTEGER REFERENCES journal_entries (id) ON DELETE RESTRICT,
    created_at         TEXT    NOT NULL DEFAULT (datetime('now', 'localtime')),

    CHECK (prices_include_tax IN (0, 1)),
    CHECK (subtotal >= 0 AND tax_total >= 0 AND total >= 0),
    CHECK (total = subtotal + CASE WHEN prices_include_tax = 1 THEN 0 ELSE tax_total END),
    CHECK (cogs_total >= 0 AND restocked_cogs >= 0 AND restocked_cogs <= cogs_total),
    CHECK (payment_method IN ('Cash', 'Card', 'Transfer', 'Credit')),
    CHECK (length(trim(reason)) > 0)
);

CREATE INDEX idx_sale_refunds_sale ON sale_refunds (sale_id);
CREATE INDEX idx_sale_refunds_session ON sale_refunds (shift_session_id, payment_method);
CREATE INDEX idx_sale_refunds_date ON sale_refunds (refund_date);

CREATE TRIGGER trg_sale_refunds_require_open_session
BEFORE INSERT ON sale_refunds
WHEN NOT EXISTS (
    SELECT 1 FROM shift_sessions
    WHERE id = NEW.shift_session_id AND status = 'Open' AND user_id = NEW.user_id
)
BEGIN
    SELECT RAISE(ABORT, 'لا يمكن تسجيل استرجاع خارج وردية مفتوحة باسم الموظف.');
END;

-- A credit invoice is settled back against the customer's account, and a paid
-- invoice can never become a debt.
CREATE TRIGGER trg_sale_refunds_method_matches_sale
BEFORE INSERT ON sale_refunds
WHEN ((SELECT payment_method FROM sales WHERE id = NEW.sale_id) = 'Credit') <> (NEW.payment_method = 'Credit')
BEGIN
    SELECT RAISE(ABORT, 'طريقة رد المبلغ لا تتوافق مع طريقة دفع الفاتورة الأصلية.');
END;

CREATE TRIGGER trg_sale_refunds_immutable
BEFORE UPDATE ON sale_refunds
WHEN NEW.refund_number <> OLD.refund_number
  OR NEW.sale_id <> OLD.sale_id
  OR NEW.refund_date <> OLD.refund_date
  OR NEW.refunded_at <> OLD.refunded_at
  OR NEW.user_id <> OLD.user_id
  OR NEW.shift_session_id <> OLD.shift_session_id
  OR NEW.prices_include_tax <> OLD.prices_include_tax
  OR NEW.subtotal <> OLD.subtotal
  OR NEW.tax_total <> OLD.tax_total
  OR NEW.total <> OLD.total
  OR NEW.cogs_total <> OLD.cogs_total
  OR NEW.restocked_cogs <> OLD.restocked_cogs
  OR NEW.payment_method <> OLD.payment_method
  OR NEW.reason <> OLD.reason
  OR (OLD.journal_entry_id IS NOT NULL AND NEW.journal_entry_id IS NOT OLD.journal_entry_id)
BEGIN
    SELECT RAISE(ABORT, 'سند الاسترجاع لا يُعدَّل بعد إصداره.');
END;

CREATE TRIGGER trg_sale_refunds_no_delete
BEFORE DELETE ON sale_refunds
BEGIN
    SELECT RAISE(ABORT, 'لا يمكن حذف سند استرجاع.');
END;

CREATE TABLE sale_refund_items (
    id             INTEGER PRIMARY KEY,
    sale_refund_id INTEGER NOT NULL REFERENCES sale_refunds (id) ON DELETE RESTRICT,
    sale_item_id   INTEGER NOT NULL REFERENCES sale_items (id) ON DELETE RESTRICT,
    product_id     INTEGER NOT NULL REFERENCES products (id) ON DELETE RESTRICT,
    quantity       INTEGER NOT NULL,
    line_total     INTEGER NOT NULL,
    line_cogs      INTEGER NOT NULL,
    -- Damaged goods are refunded without going back on the shelf.
    restocked      INTEGER NOT NULL DEFAULT 1,

    CHECK (quantity > 0),
    CHECK (line_total >= 0 AND line_cogs >= 0),
    CHECK (restocked IN (0, 1))
);

CREATE INDEX idx_sale_refund_items_refund ON sale_refund_items (sale_refund_id);

CREATE TRIGGER trg_sale_refund_items_only_while_open
BEFORE INSERT ON sale_refund_items
WHEN (SELECT journal_entry_id FROM sale_refunds WHERE id = NEW.sale_refund_id) IS NOT NULL
BEGIN
    SELECT RAISE(ABORT, 'لا يمكن إضافة سطر إلى سند استرجاع صادر.');
END;

CREATE TRIGGER trg_sale_refund_items_no_update
BEFORE UPDATE ON sale_refund_items
BEGIN
    SELECT RAISE(ABORT, 'سطر الاسترجاع لا يُعدَّل.');
END;

CREATE TRIGGER trg_sale_refund_items_no_delete
BEFORE DELETE ON sale_refund_items
BEGIN
    SELECT RAISE(ABORT, 'لا يمكن حذف سطر استرجاع.');
END;
