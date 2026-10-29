-- =============================================================================
-- 0007 - Vouchers: customer receipts, supplier payments, expense vouchers.
--
-- These close the loops the web version left open: a credit customer can now
-- settle their account, a supplier can be paid, and everyday expenses (rent,
-- electricity, transport) can be recorded by non-accountants.
--
-- Cash vouchers may pass through the till drawer (shift_session_id set) or the
-- shop's safe (shift_session_id NULL). Drawer vouchers count in that session's
-- expected cash, so the drawer still reconciles.
--
-- A voucher is never edited or deleted. A mistake is voided: the void posts a
-- reversing entry, and the original stays on record with who voided it and why.
-- A drawer voucher can only be voided while its session is still open, because
-- the closed session's count has already been agreed.
-- =============================================================================

CREATE TABLE customer_receipts (
    id                   INTEGER PRIMARY KEY,
    receipt_number       TEXT    NOT NULL UNIQUE,
    customer_id          INTEGER NOT NULL REFERENCES customers (id) ON DELETE RESTRICT,
    receipt_date         TEXT    NOT NULL,
    received_at          TEXT    NOT NULL,
    amount               INTEGER NOT NULL,
    payment_method       TEXT    NOT NULL,
    shift_session_id     INTEGER REFERENCES shift_sessions (id) ON DELETE RESTRICT,
    user_id              INTEGER NOT NULL REFERENCES users (id) ON DELETE RESTRICT,
    reference            TEXT,
    notes                TEXT,
    journal_entry_id     INTEGER REFERENCES journal_entries (id) ON DELETE RESTRICT,
    voided_at            TEXT,
    voided_by            INTEGER REFERENCES users (id) ON DELETE SET NULL,
    void_reason          TEXT,
    void_journal_entry_id INTEGER REFERENCES journal_entries (id) ON DELETE RESTRICT,
    created_at           TEXT    NOT NULL DEFAULT (datetime('now', 'localtime')),

    CHECK (amount > 0),
    CHECK (payment_method IN ('Cash', 'Card', 'Transfer')),
    CHECK (shift_session_id IS NULL OR payment_method = 'Cash'),
    CHECK (
        (voided_at IS NULL AND void_reason IS NULL AND void_journal_entry_id IS NULL)
        OR (voided_at IS NOT NULL AND length(trim(void_reason)) > 0 AND void_journal_entry_id IS NOT NULL)
    )
);

CREATE INDEX idx_customer_receipts_customer ON customer_receipts (customer_id);
CREATE INDEX idx_customer_receipts_session ON customer_receipts (shift_session_id);
CREATE INDEX idx_customer_receipts_date ON customer_receipts (receipt_date);

CREATE TABLE supplier_payments (
    id                   INTEGER PRIMARY KEY,
    payment_number       TEXT    NOT NULL UNIQUE,
    supplier_id          INTEGER NOT NULL REFERENCES suppliers (id) ON DELETE RESTRICT,
    payment_date         TEXT    NOT NULL,
    paid_at              TEXT    NOT NULL,
    amount               INTEGER NOT NULL,
    payment_method       TEXT    NOT NULL,
    shift_session_id     INTEGER REFERENCES shift_sessions (id) ON DELETE RESTRICT,
    user_id              INTEGER NOT NULL REFERENCES users (id) ON DELETE RESTRICT,
    reference            TEXT,
    notes                TEXT,
    journal_entry_id     INTEGER REFERENCES journal_entries (id) ON DELETE RESTRICT,
    voided_at            TEXT,
    voided_by            INTEGER REFERENCES users (id) ON DELETE SET NULL,
    void_reason          TEXT,
    void_journal_entry_id INTEGER REFERENCES journal_entries (id) ON DELETE RESTRICT,
    created_at           TEXT    NOT NULL DEFAULT (datetime('now', 'localtime')),

    CHECK (amount > 0),
    CHECK (payment_method IN ('Cash', 'Card', 'Transfer')),
    CHECK (shift_session_id IS NULL OR payment_method = 'Cash'),
    CHECK (
        (voided_at IS NULL AND void_reason IS NULL AND void_journal_entry_id IS NULL)
        OR (voided_at IS NOT NULL AND length(trim(void_reason)) > 0 AND void_journal_entry_id IS NOT NULL)
    )
);

CREATE INDEX idx_supplier_payments_supplier ON supplier_payments (supplier_id);
CREATE INDEX idx_supplier_payments_session ON supplier_payments (shift_session_id);
CREATE INDEX idx_supplier_payments_date ON supplier_payments (payment_date);

CREATE TABLE expense_vouchers (
    id                   INTEGER PRIMARY KEY,
    voucher_number       TEXT    NOT NULL UNIQUE,
    expense_account_id   INTEGER NOT NULL REFERENCES accounts (id) ON DELETE RESTRICT,
    voucher_date         TEXT    NOT NULL,
    paid_at              TEXT    NOT NULL,
    amount               INTEGER NOT NULL,
    payment_method       TEXT    NOT NULL,
    shift_session_id     INTEGER REFERENCES shift_sessions (id) ON DELETE RESTRICT,
    user_id              INTEGER NOT NULL REFERENCES users (id) ON DELETE RESTRICT,
    payee                TEXT,
    description          TEXT    NOT NULL,
    journal_entry_id     INTEGER REFERENCES journal_entries (id) ON DELETE RESTRICT,
    voided_at            TEXT,
    voided_by            INTEGER REFERENCES users (id) ON DELETE SET NULL,
    void_reason          TEXT,
    void_journal_entry_id INTEGER REFERENCES journal_entries (id) ON DELETE RESTRICT,
    created_at           TEXT    NOT NULL DEFAULT (datetime('now', 'localtime')),

    CHECK (amount > 0),
    CHECK (payment_method IN ('Cash', 'Card', 'Transfer')),
    CHECK (shift_session_id IS NULL OR payment_method = 'Cash'),
    CHECK (length(trim(description)) > 0),
    CHECK (
        (voided_at IS NULL AND void_reason IS NULL AND void_journal_entry_id IS NULL)
        OR (voided_at IS NOT NULL AND length(trim(void_reason)) > 0 AND void_journal_entry_id IS NOT NULL)
    )
);

CREATE INDEX idx_expense_vouchers_account ON expense_vouchers (expense_account_id);
CREATE INDEX idx_expense_vouchers_session ON expense_vouchers (shift_session_id);
CREATE INDEX idx_expense_vouchers_date ON expense_vouchers (voucher_date);

CREATE TRIGGER trg_expense_vouchers_expense_account
BEFORE INSERT ON expense_vouchers
WHEN (SELECT type FROM accounts WHERE id = NEW.expense_account_id) IS NOT 'Expense'
BEGIN
    SELECT RAISE(ABORT, 'سند المصروف يجب أن يُحمَّل على حساب من نوع مصروفات.');
END;

-- --- Shared rules, written out per table (SQLite triggers are per table) -----

CREATE TRIGGER trg_customer_receipts_drawer_session
BEFORE INSERT ON customer_receipts
WHEN NEW.shift_session_id IS NOT NULL AND NOT EXISTS (
    SELECT 1 FROM shift_sessions
    WHERE id = NEW.shift_session_id AND status = 'Open' AND user_id = NEW.user_id
)
BEGIN
    SELECT RAISE(ABORT, 'لا يمكن تسجيل حركة على درج وردية غير مفتوحة باسمك.');
END;

CREATE TRIGGER trg_supplier_payments_drawer_session
BEFORE INSERT ON supplier_payments
WHEN NEW.shift_session_id IS NOT NULL AND NOT EXISTS (
    SELECT 1 FROM shift_sessions
    WHERE id = NEW.shift_session_id AND status = 'Open' AND user_id = NEW.user_id
)
BEGIN
    SELECT RAISE(ABORT, 'لا يمكن تسجيل حركة على درج وردية غير مفتوحة باسمك.');
END;

CREATE TRIGGER trg_expense_vouchers_drawer_session
BEFORE INSERT ON expense_vouchers
WHEN NEW.shift_session_id IS NOT NULL AND NOT EXISTS (
    SELECT 1 FROM shift_sessions
    WHERE id = NEW.shift_session_id AND status = 'Open' AND user_id = NEW.user_id
)
BEGIN
    SELECT RAISE(ABORT, 'لا يمكن تسجيل حركة على درج وردية غير مفتوحة باسمك.');
END;

-- Immutable apart from linking the journal entry once and voiding once.
CREATE TRIGGER trg_customer_receipts_immutable
BEFORE UPDATE ON customer_receipts
WHEN NEW.receipt_number <> OLD.receipt_number
  OR NEW.customer_id <> OLD.customer_id
  OR NEW.receipt_date <> OLD.receipt_date
  OR NEW.received_at <> OLD.received_at
  OR NEW.amount <> OLD.amount
  OR NEW.payment_method <> OLD.payment_method
  OR NEW.shift_session_id IS NOT OLD.shift_session_id
  OR NEW.user_id <> OLD.user_id
  OR NEW.reference IS NOT OLD.reference
  OR NEW.notes IS NOT OLD.notes
  OR (OLD.journal_entry_id IS NOT NULL AND NEW.journal_entry_id IS NOT OLD.journal_entry_id)
  OR (OLD.voided_at IS NOT NULL AND (
        NEW.voided_at IS NOT OLD.voided_at OR NEW.voided_by IS NOT OLD.voided_by
     OR NEW.void_reason IS NOT OLD.void_reason OR NEW.void_journal_entry_id IS NOT OLD.void_journal_entry_id))
BEGIN
    SELECT RAISE(ABORT, 'سند القبض لا يُعدَّل بعد إصداره؛ يمكن إلغاؤه فقط.');
END;

CREATE TRIGGER trg_supplier_payments_immutable
BEFORE UPDATE ON supplier_payments
WHEN NEW.payment_number <> OLD.payment_number
  OR NEW.supplier_id <> OLD.supplier_id
  OR NEW.payment_date <> OLD.payment_date
  OR NEW.paid_at <> OLD.paid_at
  OR NEW.amount <> OLD.amount
  OR NEW.payment_method <> OLD.payment_method
  OR NEW.shift_session_id IS NOT OLD.shift_session_id
  OR NEW.user_id <> OLD.user_id
  OR NEW.reference IS NOT OLD.reference
  OR NEW.notes IS NOT OLD.notes
  OR (OLD.journal_entry_id IS NOT NULL AND NEW.journal_entry_id IS NOT OLD.journal_entry_id)
  OR (OLD.voided_at IS NOT NULL AND (
        NEW.voided_at IS NOT OLD.voided_at OR NEW.voided_by IS NOT OLD.voided_by
     OR NEW.void_reason IS NOT OLD.void_reason OR NEW.void_journal_entry_id IS NOT OLD.void_journal_entry_id))
BEGIN
    SELECT RAISE(ABORT, 'سند الصرف لا يُعدَّل بعد إصداره؛ يمكن إلغاؤه فقط.');
END;

CREATE TRIGGER trg_expense_vouchers_immutable
BEFORE UPDATE ON expense_vouchers
WHEN NEW.voucher_number <> OLD.voucher_number
  OR NEW.expense_account_id <> OLD.expense_account_id
  OR NEW.voucher_date <> OLD.voucher_date
  OR NEW.paid_at <> OLD.paid_at
  OR NEW.amount <> OLD.amount
  OR NEW.payment_method <> OLD.payment_method
  OR NEW.shift_session_id IS NOT OLD.shift_session_id
  OR NEW.user_id <> OLD.user_id
  OR NEW.payee IS NOT OLD.payee
  OR NEW.description <> OLD.description
  OR (OLD.journal_entry_id IS NOT NULL AND NEW.journal_entry_id IS NOT OLD.journal_entry_id)
  OR (OLD.voided_at IS NOT NULL AND (
        NEW.voided_at IS NOT OLD.voided_at OR NEW.voided_by IS NOT OLD.voided_by
     OR NEW.void_reason IS NOT OLD.void_reason OR NEW.void_journal_entry_id IS NOT OLD.void_journal_entry_id))
BEGIN
    SELECT RAISE(ABORT, 'سند المصروف لا يُعدَّل بعد إصداره؛ يمكن إلغاؤه فقط.');
END;

-- Voiding a drawer voucher after its session closed would unbalance a count
-- that has already been signed off.
CREATE TRIGGER trg_customer_receipts_void_open_drawer
BEFORE UPDATE OF voided_at ON customer_receipts
WHEN OLD.voided_at IS NULL AND NEW.voided_at IS NOT NULL
 AND OLD.shift_session_id IS NOT NULL
 AND (SELECT status FROM shift_sessions WHERE id = OLD.shift_session_id) <> 'Open'
BEGIN
    SELECT RAISE(ABORT, 'لا يمكن إلغاء سند على درج وردية تم إغلاقها.');
END;

CREATE TRIGGER trg_supplier_payments_void_open_drawer
BEFORE UPDATE OF voided_at ON supplier_payments
WHEN OLD.voided_at IS NULL AND NEW.voided_at IS NOT NULL
 AND OLD.shift_session_id IS NOT NULL
 AND (SELECT status FROM shift_sessions WHERE id = OLD.shift_session_id) <> 'Open'
BEGIN
    SELECT RAISE(ABORT, 'لا يمكن إلغاء سند على درج وردية تم إغلاقها.');
END;

CREATE TRIGGER trg_expense_vouchers_void_open_drawer
BEFORE UPDATE OF voided_at ON expense_vouchers
WHEN OLD.voided_at IS NULL AND NEW.voided_at IS NOT NULL
 AND OLD.shift_session_id IS NOT NULL
 AND (SELECT status FROM shift_sessions WHERE id = OLD.shift_session_id) <> 'Open'
BEGIN
    SELECT RAISE(ABORT, 'لا يمكن إلغاء سند على درج وردية تم إغلاقها.');
END;

CREATE TRIGGER trg_customer_receipts_no_delete
BEFORE DELETE ON customer_receipts
BEGIN
    SELECT RAISE(ABORT, 'لا يمكن حذف سند قبض؛ يمكن إلغاؤه فقط.');
END;

CREATE TRIGGER trg_supplier_payments_no_delete
BEFORE DELETE ON supplier_payments
BEGIN
    SELECT RAISE(ABORT, 'لا يمكن حذف سند صرف؛ يمكن إلغاؤه فقط.');
END;

CREATE TRIGGER trg_expense_vouchers_no_delete
BEFORE DELETE ON expense_vouchers
BEGIN
    SELECT RAISE(ABORT, 'لا يمكن حذف سند مصروف؛ يمكن إلغاؤه فقط.');
END;
