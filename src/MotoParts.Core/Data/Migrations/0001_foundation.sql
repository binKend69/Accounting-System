-- =============================================================================
-- 0001 - Foundation: settings, users and permissions, audit trail, numbering.
--
-- Conventions used by every migration:
--   * Money columns are INTEGER minor units (halalas/cents). SQLite has no exact
--     decimal type, and a REAL column would hold 0.10 as a binary float.
--   * Quantity columns are INTEGER thousandths (1.5 litres = 1500).
--   * Dates are TEXT 'YYYY-MM-DD'; date-times are TEXT 'YYYY-MM-DD HH:MM:SS' in
--     shop-local time. Both sort and compare correctly as text.
--   * Enums are TEXT holding the C# member name, guarded by a CHECK list.
--   * Business rules that must hold however the data is written are enforced
--     here with CHECK constraints and triggers, not only in the services. The
--     trigger messages are Arabic because they can reach the operator.
-- =============================================================================

CREATE TABLE settings (
    key        TEXT PRIMARY KEY,
    value      TEXT,
    updated_at TEXT NOT NULL DEFAULT (datetime('now', 'localtime'))
);

-- -----------------------------------------------------------------------------
-- Users, roles, permissions
-- -----------------------------------------------------------------------------

CREATE TABLE users (
    id                   INTEGER PRIMARY KEY,
    username             TEXT    NOT NULL UNIQUE COLLATE NOCASE,
    full_name            TEXT    NOT NULL,
    password_hash        TEXT    NOT NULL,
    employee_code        TEXT    UNIQUE COLLATE NOCASE,
    phone                TEXT,
    hire_date            TEXT,
    base_salary          INTEGER NOT NULL DEFAULT 0,
    -- Deactivated rather than deleted, so their sales and sessions keep a
    -- valid actor. A user with any history cannot be deleted (foreign keys).
    is_active            INTEGER NOT NULL DEFAULT 1,
    must_change_password INTEGER NOT NULL DEFAULT 0,
    last_login_at        TEXT,
    created_at           TEXT    NOT NULL DEFAULT (datetime('now', 'localtime')),
    updated_at           TEXT    NOT NULL DEFAULT (datetime('now', 'localtime')),

    CHECK (base_salary >= 0),
    CHECK (is_active IN (0, 1)),
    CHECK (must_change_password IN (0, 1)),
    CHECK (length(trim(username)) > 0),
    CHECK (length(trim(full_name)) > 0)
);

CREATE TABLE roles (
    id           INTEGER PRIMARY KEY,
    name         TEXT NOT NULL UNIQUE,
    display_name TEXT NOT NULL
);

CREATE TABLE permissions (
    id   INTEGER PRIMARY KEY,
    name TEXT NOT NULL UNIQUE
);

CREATE TABLE role_permissions (
    role_id       INTEGER NOT NULL REFERENCES roles (id) ON DELETE CASCADE,
    permission_id INTEGER NOT NULL REFERENCES permissions (id) ON DELETE CASCADE,
    PRIMARY KEY (role_id, permission_id)
);

CREATE TABLE user_roles (
    user_id INTEGER NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    role_id INTEGER NOT NULL REFERENCES roles (id) ON DELETE RESTRICT,
    PRIMARY KEY (user_id, role_id)
);

-- -----------------------------------------------------------------------------
-- Audit trail: append-only.
-- -----------------------------------------------------------------------------

CREATE TABLE audit_logs (
    id             INTEGER PRIMARY KEY,
    auditable_type TEXT    NOT NULL,
    auditable_id   INTEGER,
    action         TEXT    NOT NULL,
    old_values     TEXT,
    new_values     TEXT,
    -- Nullable, and the name is copied, so the trail still names the actor if
    -- the user row is ever removed.
    user_id        INTEGER REFERENCES users (id) ON DELETE SET NULL,
    user_name      TEXT,
    machine_name   TEXT,
    reason         TEXT,
    created_at     TEXT    NOT NULL
);

CREATE INDEX idx_audit_logs_auditable ON audit_logs (auditable_type, auditable_id);
CREATE INDEX idx_audit_logs_created ON audit_logs (created_at);
CREATE INDEX idx_audit_logs_user ON audit_logs (user_id, created_at);

-- A trail that can be edited is not a trail.
CREATE TRIGGER trg_audit_logs_no_update
BEFORE UPDATE ON audit_logs
BEGIN
    SELECT RAISE(ABORT, 'سجل التدقيق للإضافة فقط ولا يمكن تعديله.');
END;

CREATE TRIGGER trg_audit_logs_no_delete
BEFORE DELETE ON audit_logs
BEGIN
    SELECT RAISE(ABORT, 'سجل التدقيق للإضافة فقط ولا يمكن حذف أي سطر منه.');
END;

-- -----------------------------------------------------------------------------
-- Gapless document numbering.
--
-- One row per (document kind, year). The counter is read and advanced inside
-- the same write transaction as the document it numbers, so a failed document
-- rolls its number back and the series stays gapless.
-- -----------------------------------------------------------------------------

CREATE TABLE number_sequences (
    sequence_key TEXT    NOT NULL,
    year         INTEGER NOT NULL,
    next_value   INTEGER NOT NULL DEFAULT 1,
    PRIMARY KEY (sequence_key, year),
    CHECK (next_value >= 1)
);

-- A counter only moves forward. Moving it back would reissue a number.
CREATE TRIGGER trg_number_sequences_forward_only
BEFORE UPDATE ON number_sequences
WHEN NEW.next_value < OLD.next_value
BEGIN
    SELECT RAISE(ABORT, 'لا يمكن إرجاع عداد الترقيم للخلف؛ ذلك يعيد إصدار أرقام مستخدمة.');
END;
