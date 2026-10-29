-- =============================================================================
-- 0004 - Shifts: templates, till sessions, attendance.
--
-- A shift session is how cash becomes someone's responsibility. Every sale,
-- refund and drawer movement is tagged with the session it happened in, so at
-- close the drawer is counted against what the books say should be there and
-- any difference is attributed to the person on duty.
-- =============================================================================

CREATE TABLE shifts (
    id         INTEGER PRIMARY KEY,
    name       TEXT    NOT NULL UNIQUE,
    -- Template times only; a session records what actually happened.
    start_time TEXT    NOT NULL,
    end_time   TEXT    NOT NULL,
    is_active  INTEGER NOT NULL DEFAULT 1,
    created_at TEXT    NOT NULL DEFAULT (datetime('now', 'localtime')),
    updated_at TEXT    NOT NULL DEFAULT (datetime('now', 'localtime')),

    CHECK (is_active IN (0, 1)),
    CHECK (length(trim(name)) > 0)
);

CREATE TABLE shift_sessions (
    id                  INTEGER PRIMARY KEY,
    shift_id            INTEGER NOT NULL REFERENCES shifts (id) ON DELETE RESTRICT,
    user_id             INTEGER NOT NULL REFERENCES users (id) ON DELETE RESTRICT,
    opened_at           TEXT    NOT NULL,
    closed_at           TEXT,
    opening_cash        INTEGER NOT NULL,

    -- Snapshot taken at close, stored rather than recomputed so the closing
    -- report always shows the figures that were agreed at the time.
    cash_sales_total    INTEGER,
    cash_refunds_total  INTEGER,
    -- Cash taken into the drawer outside a sale (e.g. a customer settling an
    -- account) and paid out of it (e.g. a small expense).
    cash_receipts_total INTEGER,
    cash_payouts_total  INTEGER,
    expected_cash       INTEGER,
    actual_closing_cash INTEGER,
    -- actual - expected. Negative means the drawer is short.
    cash_difference     INTEGER,

    status              TEXT    NOT NULL DEFAULT 'Open',
    closed_by           INTEGER REFERENCES users (id) ON DELETE SET NULL,
    override_reason     TEXT,
    notes               TEXT,
    created_at          TEXT    NOT NULL DEFAULT (datetime('now', 'localtime')),
    updated_at          TEXT    NOT NULL DEFAULT (datetime('now', 'localtime')),

    CHECK (status IN ('Open', 'Closed', 'ForceClosed')),
    CHECK (opening_cash >= 0),
    CHECK (actual_closing_cash IS NULL OR actual_closing_cash >= 0),
    -- An open session has no closing figures; a closed one has all of them.
    CHECK (
        (status = 'Open'
            AND closed_at IS NULL AND expected_cash IS NULL
            AND actual_closing_cash IS NULL AND cash_difference IS NULL)
        OR
        (status <> 'Open'
            AND closed_at IS NOT NULL
            AND cash_sales_total IS NOT NULL AND cash_refunds_total IS NOT NULL
            AND cash_receipts_total IS NOT NULL AND cash_payouts_total IS NOT NULL
            AND expected_cash IS NOT NULL AND actual_closing_cash IS NOT NULL
            AND cash_difference IS NOT NULL)
    ),
    -- The reconciliation arithmetic, held by the database itself.
    CHECK (
        expected_cash IS NULL
        OR expected_cash = opening_cash + cash_sales_total - cash_refunds_total
                         + cash_receipts_total - cash_payouts_total
    ),
    CHECK (cash_difference IS NULL OR cash_difference = actual_closing_cash - expected_cash),
    CHECK (closed_at IS NULL OR closed_at >= opened_at)
);

-- One open session per employee. A partial unique index rather than a
-- read-then-insert check, so it holds whoever writes the row.
CREATE UNIQUE INDEX uniq_one_open_session_per_user
    ON shift_sessions (user_id)
    WHERE status = 'Open';

CREATE INDEX idx_shift_sessions_user ON shift_sessions (user_id, status);
CREATE INDEX idx_shift_sessions_opened ON shift_sessions (opened_at);

CREATE TRIGGER trg_shift_sessions_identity_frozen
BEFORE UPDATE ON shift_sessions
WHEN NEW.shift_id <> OLD.shift_id
  OR NEW.user_id <> OLD.user_id
  OR NEW.opened_at <> OLD.opened_at
  OR NEW.opening_cash <> OLD.opening_cash
BEGIN
    SELECT RAISE(ABORT, 'لا يمكن تغيير صاحب الوردية أو وقت فتحها أو مبلغ العهدة بعد الفتح.');
END;

CREATE TRIGGER trg_shift_sessions_no_reopen
BEFORE UPDATE OF status ON shift_sessions
WHEN OLD.status <> 'Open' AND NEW.status = 'Open'
BEGIN
    SELECT RAISE(ABORT, 'لا يمكن إعادة فتح وردية مغلقة.');
END;

-- Once counted, the expected figure is the agreed baseline. Only an owner's
-- recount (actual cash, and the difference that follows) may change it.
CREATE TRIGGER trg_shift_sessions_snapshot_frozen
BEFORE UPDATE ON shift_sessions
WHEN OLD.status <> 'Open'
 AND (NEW.status <> OLD.status
   OR NEW.closed_at IS NOT OLD.closed_at
   OR NEW.cash_sales_total IS NOT OLD.cash_sales_total
   OR NEW.cash_refunds_total IS NOT OLD.cash_refunds_total
   OR NEW.cash_receipts_total IS NOT OLD.cash_receipts_total
   OR NEW.cash_payouts_total IS NOT OLD.cash_payouts_total
   OR NEW.expected_cash IS NOT OLD.expected_cash)
BEGIN
    SELECT RAISE(ABORT, 'أرقام إغلاق الوردية ثابتة بعد الإغلاق؛ يُسمح فقط بتصحيح المبلغ المعدود.');
END;

CREATE TRIGGER trg_shift_sessions_no_delete
BEFORE DELETE ON shift_sessions
BEGIN
    SELECT RAISE(ABORT, 'لا يمكن حذف وردية.');
END;

-- -----------------------------------------------------------------------------
-- Attendance: independent of tills. A mechanic clocks in without a drawer.
-- -----------------------------------------------------------------------------

CREATE TABLE attendances (
    id           INTEGER PRIMARY KEY,
    user_id      INTEGER NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    work_date    TEXT    NOT NULL,
    check_in_at  TEXT,
    check_out_at TEXT,
    notes        TEXT,
    created_at   TEXT    NOT NULL DEFAULT (datetime('now', 'localtime')),
    updated_at   TEXT    NOT NULL DEFAULT (datetime('now', 'localtime')),

    UNIQUE (user_id, work_date),
    CHECK (check_out_at IS NULL OR check_in_at IS NOT NULL),
    CHECK (check_out_at IS NULL OR check_out_at >= check_in_at)
);

CREATE INDEX idx_attendances_date ON attendances (work_date);
