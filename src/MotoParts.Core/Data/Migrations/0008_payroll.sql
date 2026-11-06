-- =============================================================================
-- 0008 - Payroll: a monthly run per calendar month.
--
--   Draft  -> freely edited; one line per employee: base + additions - deductions
--   Posted -> Dr Salaries Expense / Cr Salaries Payable; amounts frozen
--   Paid   -> Dr Salaries Payable / Cr Cash or Bank
--
-- The run number is allocated when the run is posted, so discarding a draft
-- leaves no gap in the series.
-- =============================================================================

CREATE TABLE payroll_runs (
    id                       INTEGER PRIMARY KEY,
    run_number               TEXT    UNIQUE,
    period_year              INTEGER NOT NULL,
    period_month             INTEGER NOT NULL,
    status                   TEXT    NOT NULL DEFAULT 'Draft',
    total_base               INTEGER NOT NULL DEFAULT 0,
    total_additions          INTEGER NOT NULL DEFAULT 0,
    total_deductions         INTEGER NOT NULL DEFAULT 0,
    total_net                INTEGER NOT NULL DEFAULT 0,
    posting_date             TEXT,
    posted_at                TEXT,
    posted_by                INTEGER REFERENCES users (id) ON DELETE SET NULL,
    journal_entry_id         INTEGER REFERENCES journal_entries (id) ON DELETE RESTRICT,
    payment_date             TEXT,
    paid_at                  TEXT,
    paid_by                  INTEGER REFERENCES users (id) ON DELETE SET NULL,
    payment_method           TEXT,
    payment_journal_entry_id INTEGER REFERENCES journal_entries (id) ON DELETE RESTRICT,
    notes                    TEXT,
    created_by               INTEGER REFERENCES users (id) ON DELETE SET NULL,
    created_at               TEXT    NOT NULL DEFAULT (datetime('now', 'localtime')),
    updated_at               TEXT    NOT NULL DEFAULT (datetime('now', 'localtime')),

    -- One payroll per month: paying a month twice is the classic payroll error.
    UNIQUE (period_year, period_month),
    CHECK (period_month BETWEEN 1 AND 12),
    CHECK (period_year BETWEEN 2000 AND 2100),
    CHECK (status IN ('Draft', 'Posted', 'Paid')),
    CHECK (total_base >= 0 AND total_additions >= 0 AND total_deductions >= 0 AND total_net >= 0),
    CHECK (total_net = total_base + total_additions - total_deductions),
    CHECK (status = 'Draft' OR (run_number IS NOT NULL AND posted_at IS NOT NULL AND journal_entry_id IS NOT NULL)),
    CHECK (status <> 'Paid' OR (paid_at IS NOT NULL AND payment_journal_entry_id IS NOT NULL)),
    CHECK (payment_method IS NULL OR payment_method IN ('Cash', 'Card', 'Transfer'))
);

CREATE TABLE payroll_lines (
    id             INTEGER PRIMARY KEY,
    payroll_run_id INTEGER NOT NULL REFERENCES payroll_runs (id) ON DELETE CASCADE,
    user_id        INTEGER NOT NULL REFERENCES users (id) ON DELETE RESTRICT,
    -- Copied from the employee when the run was drafted.
    employee_name  TEXT    NOT NULL,
    base_salary    INTEGER NOT NULL,
    additions      INTEGER NOT NULL DEFAULT 0,
    deductions     INTEGER NOT NULL DEFAULT 0,
    net_pay        INTEGER NOT NULL,
    notes          TEXT,

    UNIQUE (payroll_run_id, user_id),
    CHECK (base_salary >= 0 AND additions >= 0 AND deductions >= 0),
    CHECK (net_pay = base_salary + additions - deductions),
    CHECK (net_pay >= 0)
);

CREATE INDEX idx_payroll_lines_user ON payroll_lines (user_id);

CREATE TRIGGER trg_payroll_runs_posted_frozen
BEFORE UPDATE ON payroll_runs
WHEN OLD.status <> 'Draft'
 AND (NEW.total_base <> OLD.total_base
   OR NEW.total_additions <> OLD.total_additions
   OR NEW.total_deductions <> OLD.total_deductions
   OR NEW.total_net <> OLD.total_net
   OR NEW.period_year <> OLD.period_year
   OR NEW.period_month <> OLD.period_month
   OR NEW.run_number IS NOT OLD.run_number
   OR NEW.journal_entry_id IS NOT OLD.journal_entry_id)
BEGIN
    SELECT RAISE(ABORT, 'مسير الرواتب معتمد ولا يمكن تعديل مبالغه.');
END;

CREATE TRIGGER trg_payroll_runs_status_forward
BEFORE UPDATE OF status ON payroll_runs
WHEN NOT (
    NEW.status = OLD.status
    OR (OLD.status = 'Draft' AND NEW.status = 'Posted')
    OR (OLD.status = 'Posted' AND NEW.status = 'Paid')
)
BEGIN
    SELECT RAISE(ABORT, 'انتقال غير مسموح في حالة مسير الرواتب.');
END;

CREATE TRIGGER trg_payroll_runs_paid_frozen
BEFORE UPDATE ON payroll_runs
WHEN OLD.status = 'Paid'
BEGIN
    SELECT RAISE(ABORT, 'مسير الرواتب مصروف ولا يمكن تعديله.');
END;

CREATE TRIGGER trg_payroll_runs_delete_draft_only
BEFORE DELETE ON payroll_runs
WHEN OLD.status <> 'Draft'
BEGIN
    SELECT RAISE(ABORT, 'لا يمكن حذف مسير رواتب معتمد.');
END;

CREATE TRIGGER trg_payroll_lines_insert_draft_only
BEFORE INSERT ON payroll_lines
WHEN (SELECT status FROM payroll_runs WHERE id = NEW.payroll_run_id) <> 'Draft'
BEGIN
    SELECT RAISE(ABORT, 'لا يمكن تعديل مسير رواتب معتمد.');
END;

CREATE TRIGGER trg_payroll_lines_update_draft_only
BEFORE UPDATE ON payroll_lines
WHEN (SELECT status FROM payroll_runs WHERE id = OLD.payroll_run_id) <> 'Draft'
BEGIN
    SELECT RAISE(ABORT, 'لا يمكن تعديل مسير رواتب معتمد.');
END;

CREATE TRIGGER trg_payroll_lines_delete_draft_only
BEFORE DELETE ON payroll_lines
WHEN (SELECT status FROM payroll_runs WHERE id = OLD.payroll_run_id) <> 'Draft'
BEGIN
    SELECT RAISE(ABORT, 'لا يمكن تعديل مسير رواتب معتمد.');
END;
