-- =============================================================================
-- 0002 - Accounting: chart of accounts, fiscal periods, journal.
--
-- The double-entry rules live here as well as in AccountingService. The service
-- checks first so the operator gets a readable message; these triggers are the
-- guarantee, and they hold for any write, from any code, however it is made.
-- =============================================================================

CREATE TABLE accounts (
    id             INTEGER PRIMARY KEY,
    code           TEXT    NOT NULL UNIQUE,
    name           TEXT    NOT NULL,
    type           TEXT    NOT NULL,
    normal_balance TEXT    NOT NULL,
    parent_id      INTEGER REFERENCES accounts (id) ON DELETE SET NULL,
    description    TEXT,
    -- System accounts are resolved by code from the services.
    is_system      INTEGER NOT NULL DEFAULT 0,
    is_active      INTEGER NOT NULL DEFAULT 1,
    created_at     TEXT    NOT NULL DEFAULT (datetime('now', 'localtime')),
    updated_at     TEXT    NOT NULL DEFAULT (datetime('now', 'localtime')),

    CHECK (type IN ('Asset', 'Liability', 'Equity', 'Revenue', 'Expense')),
    CHECK (normal_balance IN ('Debit', 'Credit')),
    -- Normal balance follows from the type. Letting them disagree would
    -- silently invert every balance and report for the account.
    CHECK (
        (type IN ('Asset', 'Expense') AND normal_balance = 'Debit')
        OR (type IN ('Liability', 'Equity', 'Revenue') AND normal_balance = 'Credit')
    ),
    CHECK (is_system IN (0, 1)),
    CHECK (is_active IN (0, 1)),
    CHECK (length(trim(code)) > 0)
);

CREATE INDEX idx_accounts_type ON accounts (type, is_active);

CREATE TRIGGER trg_accounts_no_delete_system
BEFORE DELETE ON accounts
WHEN OLD.is_system = 1
BEGIN
    SELECT RAISE(ABORT, 'لا يمكن حذف حساب نظامي.');
END;

CREATE TRIGGER trg_accounts_system_identity
BEFORE UPDATE ON accounts
WHEN OLD.is_system = 1
 AND (NEW.code <> OLD.code OR NEW.type <> OLD.type OR NEW.is_system <> OLD.is_system)
BEGIN
    SELECT RAISE(ABORT, 'لا يمكن تغيير رمز أو نوع حساب نظامي؛ النظام يعتمد عليه.');
END;

-- Retyping an account with history would rewrite every report it appears in.
CREATE TRIGGER trg_accounts_type_frozen_with_history
BEFORE UPDATE OF type ON accounts
WHEN NEW.type <> OLD.type
 AND EXISTS (SELECT 1 FROM journal_lines WHERE account_id = OLD.id)
BEGIN
    SELECT RAISE(ABORT, 'لا يمكن تغيير نوع حساب عليه حركات مسجلة.');
END;

-- -----------------------------------------------------------------------------
-- Fiscal periods
-- -----------------------------------------------------------------------------

CREATE TABLE fiscal_periods (
    id         INTEGER PRIMARY KEY,
    name       TEXT    NOT NULL,
    start_date TEXT    NOT NULL,
    end_date   TEXT    NOT NULL,
    is_locked  INTEGER NOT NULL DEFAULT 0,
    locked_at  TEXT,
    locked_by  INTEGER REFERENCES users (id) ON DELETE SET NULL,
    lock_note  TEXT,
    created_at TEXT    NOT NULL DEFAULT (datetime('now', 'localtime')),
    updated_at TEXT    NOT NULL DEFAULT (datetime('now', 'localtime')),

    UNIQUE (start_date, end_date),
    CHECK (start_date <= end_date),
    CHECK (is_locked IN (0, 1)),
    CHECK ((is_locked = 1) = (locked_at IS NOT NULL))
);

-- Two periods covering the same day would make "which period does this entry
-- belong to" ambiguous, and with it every lock.
CREATE TRIGGER trg_fiscal_periods_no_overlap_insert
BEFORE INSERT ON fiscal_periods
WHEN EXISTS (
    SELECT 1 FROM fiscal_periods
    WHERE start_date <= NEW.end_date AND end_date >= NEW.start_date
)
BEGIN
    SELECT RAISE(ABORT, 'الفترة المحاسبية تتداخل مع فترة موجودة.');
END;

CREATE TRIGGER trg_fiscal_periods_no_overlap_update
BEFORE UPDATE OF start_date, end_date ON fiscal_periods
WHEN EXISTS (
    SELECT 1 FROM fiscal_periods
    WHERE id <> OLD.id AND start_date <= NEW.end_date AND end_date >= NEW.start_date
)
BEGIN
    SELECT RAISE(ABORT, 'الفترة المحاسبية تتداخل مع فترة موجودة.');
END;

CREATE TRIGGER trg_fiscal_periods_dates_frozen_with_entries
BEFORE UPDATE OF start_date, end_date ON fiscal_periods
WHEN (NEW.start_date <> OLD.start_date OR NEW.end_date <> OLD.end_date)
 AND EXISTS (SELECT 1 FROM journal_entries WHERE fiscal_period_id = OLD.id)
BEGIN
    SELECT RAISE(ABORT, 'لا يمكن تغيير تواريخ فترة محاسبية عليها قيود.');
END;

-- A locked period can only be unlocked, not edited while locked.
CREATE TRIGGER trg_fiscal_periods_locked_frozen
BEFORE UPDATE ON fiscal_periods
WHEN OLD.is_locked = 1 AND NEW.is_locked = 1
 AND (NEW.name <> OLD.name OR NEW.start_date <> OLD.start_date OR NEW.end_date <> OLD.end_date)
BEGIN
    SELECT RAISE(ABORT, 'الفترة المحاسبية مقفلة؛ أعد فتحها أولاً لتعديلها.');
END;

-- -----------------------------------------------------------------------------
-- Journal entries
-- -----------------------------------------------------------------------------

CREATE TABLE journal_entries (
    id                        INTEGER PRIMARY KEY,
    entry_number              TEXT    NOT NULL UNIQUE,
    fiscal_period_id          INTEGER NOT NULL REFERENCES fiscal_periods (id) ON DELETE RESTRICT,
    entry_date                TEXT    NOT NULL,
    memo                      TEXT    NOT NULL,
    source                    TEXT    NOT NULL DEFAULT 'Manual',
    -- What produced the entry (Sale, Purchase, ...). Null for manual entries.
    source_document_type      TEXT,
    source_document_id        INTEGER,
    -- Maintained by the journal_lines triggers. The posting trigger checks the
    -- lines themselves, so these can never be used to fake a balance.
    total_debit               INTEGER NOT NULL DEFAULT 0,
    total_credit              INTEGER NOT NULL DEFAULT 0,
    -- NULL = draft still being assembled; set = posted and immutable.
    posted_at                 TEXT,
    reverses_journal_entry_id INTEGER REFERENCES journal_entries (id) ON DELETE RESTRICT,
    is_reversal               INTEGER NOT NULL DEFAULT 0,
    created_by                INTEGER REFERENCES users (id) ON DELETE SET NULL,
    created_at                TEXT    NOT NULL DEFAULT (datetime('now', 'localtime')),
    updated_at                TEXT    NOT NULL DEFAULT (datetime('now', 'localtime')),

    CHECK (source IN (
        'Manual', 'Opening', 'Sale', 'SaleRefund', 'Purchase', 'StockAdjustment',
        'CashDifference', 'CustomerReceipt', 'SupplierPayment', 'Expense',
        'Payroll', 'PayrollPayment'
    )),
    CHECK (total_debit >= 0 AND total_credit >= 0),
    CHECK (is_reversal IN (0, 1)),
    CHECK ((is_reversal = 1) = (reverses_journal_entry_id IS NOT NULL))
);

CREATE INDEX idx_journal_entries_date ON journal_entries (entry_date, posted_at);
CREATE INDEX idx_journal_entries_period ON journal_entries (fiscal_period_id);
CREATE INDEX idx_journal_entries_source ON journal_entries (source_document_type, source_document_id);

-- An entry can be reversed once. A second reversal would double the correction.
CREATE UNIQUE INDEX uniq_journal_entries_reversed_once
    ON journal_entries (reverses_journal_entry_id)
    WHERE reverses_journal_entry_id IS NOT NULL;

-- Born as a draft, inside an open period that actually covers its date.
CREATE TRIGGER trg_journal_entries_insert_guard
BEFORE INSERT ON journal_entries
BEGIN
    SELECT RAISE(ABORT, 'يجب إنشاء القيد كمسودة ثم ترحيله بعد إضافة أسطره.')
    WHERE NEW.posted_at IS NOT NULL;

    SELECT RAISE(ABORT, 'لا يمكن إنشاء قيد بإجماليات مسبقة؛ الإجماليات تُحسب من الأسطر.')
    WHERE NEW.total_debit <> 0 OR NEW.total_credit <> 0;

    SELECT RAISE(ABORT, 'الفترة المحاسبية مقفلة: لا يمكن إنشاء قيد فيها.')
    WHERE (SELECT is_locked FROM fiscal_periods WHERE id = NEW.fiscal_period_id) = 1;

    SELECT RAISE(ABORT, 'تاريخ القيد خارج الفترة المحاسبية المحددة له.')
    WHERE NOT EXISTS (
        SELECT 1 FROM fiscal_periods
        WHERE id = NEW.fiscal_period_id
          AND NEW.entry_date BETWEEN start_date AND end_date
    );
END;

-- The gate that makes double entry non-negotiable.
CREATE TRIGGER trg_journal_entries_update_guard
BEFORE UPDATE ON journal_entries
BEGIN
    SELECT RAISE(ABORT, 'الفترة المحاسبية مقفلة: لا يمكن تعديل هذا القيد.')
    WHERE (SELECT is_locked FROM fiscal_periods WHERE id = OLD.fiscal_period_id) = 1;

    SELECT RAISE(ABORT, 'القيد المرحّل لا يمكن تعديله. سجّل قيداً عكسياً بدلاً من ذلك.')
    WHERE OLD.posted_at IS NOT NULL;

    SELECT RAISE(ABORT, 'لا يمكن تغيير رقم القيد أو تاريخه أو فترته بعد إنشائه.')
    WHERE NEW.entry_number <> OLD.entry_number
       OR NEW.entry_date <> OLD.entry_date
       OR NEW.fiscal_period_id <> OLD.fiscal_period_id;

    -- Posting: verified against the lines themselves, not the cached totals.
    SELECT RAISE(ABORT, 'القيد غير متوازن: مجموع المدين لا يساوي مجموع الدائن.')
    WHERE NEW.posted_at IS NOT NULL
      AND (SELECT COALESCE(SUM(debit), 0) FROM journal_lines WHERE journal_entry_id = OLD.id)
       <> (SELECT COALESCE(SUM(credit), 0) FROM journal_lines WHERE journal_entry_id = OLD.id);

    SELECT RAISE(ABORT, 'القيد بلا أسطر: لا يوجد ما يمكن ترحيله.')
    WHERE NEW.posted_at IS NOT NULL
      AND (SELECT COALESCE(SUM(debit), 0) FROM journal_lines WHERE journal_entry_id = OLD.id) <= 0;

    SELECT RAISE(ABORT, 'إجماليات القيد لا تطابق أسطره.')
    WHERE NEW.posted_at IS NOT NULL
      AND (NEW.total_debit <> (SELECT COALESCE(SUM(debit), 0) FROM journal_lines WHERE journal_entry_id = OLD.id)
        OR NEW.total_credit <> (SELECT COALESCE(SUM(credit), 0) FROM journal_lines WHERE journal_entry_id = OLD.id));
END;

CREATE TRIGGER trg_journal_entries_delete_guard
BEFORE DELETE ON journal_entries
BEGIN
    SELECT RAISE(ABORT, 'الفترة المحاسبية مقفلة: لا يمكن حذف هذا القيد.')
    WHERE (SELECT is_locked FROM fiscal_periods WHERE id = OLD.fiscal_period_id) = 1;

    SELECT RAISE(ABORT, 'لا يمكن حذف قيد مرحّل. سجّل قيداً عكسياً بدلاً من ذلك.')
    WHERE OLD.posted_at IS NOT NULL;
END;

-- -----------------------------------------------------------------------------
-- Journal lines
-- -----------------------------------------------------------------------------

CREATE TABLE journal_lines (
    id               INTEGER PRIMARY KEY,
    journal_entry_id INTEGER NOT NULL REFERENCES journal_entries (id) ON DELETE CASCADE,
    -- RESTRICT is what stops an account with history from being deleted.
    account_id       INTEGER NOT NULL REFERENCES accounts (id) ON DELETE RESTRICT,
    line_number      INTEGER NOT NULL,
    debit            INTEGER NOT NULL DEFAULT 0,
    credit           INTEGER NOT NULL DEFAULT 0,
    memo             TEXT,
    created_at       TEXT    NOT NULL DEFAULT (datetime('now', 'localtime')),

    UNIQUE (journal_entry_id, line_number),
    -- Strictly one-sided and non-zero. A negative debit is really a credit, and
    -- allowing both spellings would make every ledger total ambiguous.
    CHECK (debit >= 0 AND credit >= 0),
    CHECK (debit = 0 OR credit = 0),
    CHECK (debit + credit > 0)
);

CREATE INDEX idx_journal_lines_account ON journal_lines (account_id, journal_entry_id);

CREATE TRIGGER trg_journal_lines_insert_guard
BEFORE INSERT ON journal_lines
BEGIN
    SELECT RAISE(ABORT, 'الفترة المحاسبية مقفلة: لا يمكن إضافة سطر قيد.')
    WHERE (
        SELECT fp.is_locked FROM journal_entries je
        JOIN fiscal_periods fp ON fp.id = je.fiscal_period_id
        WHERE je.id = NEW.journal_entry_id
    ) = 1;

    SELECT RAISE(ABORT, 'لا يمكن إضافة سطر إلى قيد مرحّل.')
    WHERE (SELECT posted_at FROM journal_entries WHERE id = NEW.journal_entry_id) IS NOT NULL;
END;

CREATE TRIGGER trg_journal_lines_after_insert
AFTER INSERT ON journal_lines
BEGIN
    UPDATE journal_entries
    SET total_debit = total_debit + NEW.debit,
        total_credit = total_credit + NEW.credit
    WHERE id = NEW.journal_entry_id;
END;

CREATE TRIGGER trg_journal_lines_update_guard
BEFORE UPDATE ON journal_lines
BEGIN
    SELECT RAISE(ABORT, 'الفترة المحاسبية مقفلة: لا يمكن تعديل سطر قيد.')
    WHERE (
        SELECT fp.is_locked FROM journal_entries je
        JOIN fiscal_periods fp ON fp.id = je.fiscal_period_id
        WHERE je.id = OLD.journal_entry_id
    ) = 1;

    SELECT RAISE(ABORT, 'لا يمكن تعديل سطر في قيد مرحّل.')
    WHERE (SELECT posted_at FROM journal_entries WHERE id = OLD.journal_entry_id) IS NOT NULL;

    SELECT RAISE(ABORT, 'لا يمكن نقل سطر إلى قيد آخر.')
    WHERE NEW.journal_entry_id <> OLD.journal_entry_id;
END;

CREATE TRIGGER trg_journal_lines_after_update
AFTER UPDATE ON journal_lines
BEGIN
    UPDATE journal_entries
    SET total_debit = total_debit - OLD.debit + NEW.debit,
        total_credit = total_credit - OLD.credit + NEW.credit
    WHERE id = NEW.journal_entry_id;
END;

CREATE TRIGGER trg_journal_lines_delete_guard
BEFORE DELETE ON journal_lines
BEGIN
    SELECT RAISE(ABORT, 'الفترة المحاسبية مقفلة: لا يمكن حذف سطر قيد.')
    WHERE (
        SELECT fp.is_locked FROM journal_entries je
        JOIN fiscal_periods fp ON fp.id = je.fiscal_period_id
        WHERE je.id = OLD.journal_entry_id
    ) = 1;

    SELECT RAISE(ABORT, 'لا يمكن حذف سطر من قيد مرحّل.')
    WHERE (SELECT posted_at FROM journal_entries WHERE id = OLD.journal_entry_id) IS NOT NULL;
END;

CREATE TRIGGER trg_journal_lines_after_delete
AFTER DELETE ON journal_lines
BEGIN
    UPDATE journal_entries
    SET total_debit = total_debit - OLD.debit,
        total_credit = total_credit - OLD.credit
    WHERE id = OLD.journal_entry_id;
END;
