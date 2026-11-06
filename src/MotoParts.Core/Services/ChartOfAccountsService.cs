using MotoParts.Core.Common;
using MotoParts.Core.Data;
using MotoParts.Core.Domain;
using MotoParts.Core.Security;

namespace MotoParts.Core.Services;

/// <summary>
/// The chart of accounts, and the journal as a browsable list.
///
/// System accounts (the ones the services post to by code) can be renamed but
/// never retyped, recoded or deleted; the schema enforces the same.
/// </summary>
public sealed class ChartOfAccountsService
{
    private readonly DbSession _db;
    private readonly IClock _clock;
    private readonly AuditService _audit;

    public ChartOfAccountsService(DbSession db, IClock clock, AuditService audit)
    {
        _db = db;
        _clock = clock;
        _audit = audit;
    }

    /// <summary>Every account with its posted balance in its own normal direction.</summary>
    public IReadOnlyList<AccountRow> Accounts(bool activeOnly = false) =>
        _db.Query<AccountRow>(
            """
            SELECT a.id, a.code, a.name, a.type, a.normal_balance, a.parent_id, a.description, a.is_system, a.is_active,
                   p.code AS parent_code,
                   CASE WHEN a.normal_balance = 'Debit'
                        THEN COALESCE(SUM(l.debit), 0) - COALESCE(SUM(l.credit), 0)
                        ELSE COALESCE(SUM(l.credit), 0) - COALESCE(SUM(l.debit), 0) END AS balance,
                   COUNT(l.id) AS line_count
            FROM accounts a
            LEFT JOIN accounts p ON p.id = a.parent_id
            LEFT JOIN journal_lines l ON l.account_id = a.id
                AND EXISTS (SELECT 1 FROM journal_entries e WHERE e.id = l.journal_entry_id AND e.posted_at IS NOT NULL)
            WHERE (@activeOnly = 0 OR a.is_active = 1)
            GROUP BY a.id
            ORDER BY a.code
            """,
            new { activeOnly }).ToList();

    public Account Create(AccountInput input, Actor actor)
    {
        actor.Require(Permission.AccountingManageAccounts, "إدارة دليل الحسابات من صلاحيات المالك.");
        Validate(input);

        return _db.InTransaction(() =>
        {
            if (_db.ExecuteScalar<long>("SELECT COUNT(*) FROM accounts WHERE code = @code", new { code = input.Code.Trim() }) > 0)
            {
                throw new BusinessRuleException($"رمز الحساب {input.Code.Trim()} مستخدم.");
            }

            var id = _db.Insert(
                """
                INSERT INTO accounts (code, name, type, normal_balance, parent_id, description, is_system, is_active, created_at, updated_at)
                VALUES (@code, @name, @type, @normal, @parentId, @description, 0, @isActive, @now, @now)
                RETURNING id
                """,
                new
                {
                    code = input.Code.Trim(),
                    name = input.Name.Trim(),
                    type = input.Type.ToString(),
                    normal = input.Type.NormalBalance().ToString(),
                    parentId = input.ParentId,
                    description = Clean(input.Description),
                    isActive = input.IsActive,
                    now = _clock.Now,
                });

            _audit.Record(nameof(Account), id, AuditAction.Created, actor, newValues: input);
            return Get(id);
        });
    }

    public Account Update(long id, AccountInput input, Actor actor)
    {
        actor.Require(Permission.AccountingManageAccounts, "إدارة دليل الحسابات من صلاحيات المالك.");
        Validate(input);

        return _db.InTransaction(() =>
        {
            var before = Get(id);

            if (before.IsSystem && (input.Code.Trim() != before.Code || input.Type != before.Type))
            {
                throw new BusinessRuleException("لا يمكن تغيير رمز أو نوع حساب نظامي؛ يمكن تعديل اسمه ووصفه فقط.");
            }

            if (before.IsSystem && !input.IsActive)
            {
                throw new BusinessRuleException("لا يمكن إيقاف حساب نظامي.");
            }

            if (input.ParentId == id)
            {
                throw new BusinessRuleException("لا يمكن أن يكون الحساب أباً لنفسه.");
            }

            if (_db.ExecuteScalar<long>("SELECT COUNT(*) FROM accounts WHERE code = @code AND id <> @id", new { code = input.Code.Trim(), id }) > 0)
            {
                throw new BusinessRuleException($"رمز الحساب {input.Code.Trim()} مستخدم.");
            }

            _db.Execute(
                """
                UPDATE accounts SET code = @code, name = @name, type = @type, normal_balance = @normal,
                    parent_id = @parentId, description = @description, is_active = @isActive, updated_at = @now
                WHERE id = @id
                """,
                new
                {
                    id,
                    code = input.Code.Trim(),
                    name = input.Name.Trim(),
                    type = input.Type.ToString(),
                    normal = input.Type.NormalBalance().ToString(),
                    parentId = input.ParentId,
                    description = Clean(input.Description),
                    isActive = input.IsActive,
                    now = _clock.Now,
                });

            _audit.Record(nameof(Account), id, AuditAction.Updated, actor,
                new { before.Code, before.Name, before.Type, before.IsActive }, new { input.Code, input.Name, input.Type, input.IsActive });

            return Get(id);
        });
    }

    /// <summary>Deletes an account that has never been used. Anything else is deactivated instead.</summary>
    public void Delete(long id, Actor actor)
    {
        actor.Require(Permission.AccountingManageAccounts, "إدارة دليل الحسابات من صلاحيات المالك.");

        _db.InTransaction(() =>
        {
            var account = Get(id);

            if (account.IsSystem)
            {
                throw new BusinessRuleException("لا يمكن حذف حساب نظامي.");
            }

            var used = _db.ExecuteScalar<long>(
                """
                SELECT (SELECT COUNT(*) FROM journal_lines WHERE account_id = @id)
                     + (SELECT COUNT(*) FROM expense_vouchers WHERE expense_account_id = @id)
                     + (SELECT COUNT(*) FROM accounts WHERE parent_id = @id)
                """,
                new { id });

            if (used > 0)
            {
                throw new BusinessRuleException("الحساب مستخدم في قيود أو سندات أو له حسابات فرعية؛ أوقفه بدلاً من حذفه.");
            }

            _db.Execute("DELETE FROM accounts WHERE id = @id", new { id });
            _audit.Record(nameof(Account), id, AuditAction.Deleted, actor, new { account.Code, account.Name });
        });
    }

    public Account Get(long id) =>
        _db.QueryFirstOrDefault<Account>("SELECT * FROM accounts WHERE id = @id", new { id })
        ?? throw new BusinessRuleException("الحساب غير موجود.");

    /// <summary>Journal entries, newest first, with filters.</summary>
    public IReadOnlyList<JournalEntryRow> Entries(DateOnly? from, DateOnly? to, JournalSource? source = null, string? search = null) =>
        _db.Query<JournalEntryRow>(
            """
            SELECT e.id, e.entry_number, e.entry_date, e.memo, e.source, e.total_debit, e.posted_at, e.is_reversal,
                   e.reverses_journal_entry_id, u.full_name AS created_by_name,
                   (SELECT r.entry_number FROM journal_entries r WHERE r.reverses_journal_entry_id = e.id) AS reversed_by_number
            FROM journal_entries e
            LEFT JOIN users u ON u.id = e.created_by
            WHERE (@from IS NULL OR e.entry_date >= @from)
              AND (@to IS NULL OR e.entry_date <= @to)
              AND (@source IS NULL OR e.source = @source)
              AND (@search IS NULL OR e.entry_number LIKE @like OR e.memo LIKE @like)
            ORDER BY e.id DESC
            LIMIT 5000
            """,
            new { from, to, source = source?.ToString(), search = Clean(search), like = $"%{search?.Trim()}%" }).ToList();

    /// <summary>The lines of one entry with account codes and names, for the entry viewer.</summary>
    public IReadOnlyList<JournalLineRow> EntryLines(long entryId) =>
        _db.Query<JournalLineRow>(
            """
            SELECT l.line_number, a.code AS account_code, a.name AS account_name, l.debit, l.credit, l.memo
            FROM journal_lines l JOIN accounts a ON a.id = l.account_id
            WHERE l.journal_entry_id = @entryId ORDER BY l.line_number
            """,
            new { entryId }).ToList();

    private static void Validate(AccountInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Code))
        {
            throw new BusinessRuleException("رمز الحساب مطلوب.");
        }

        if (string.IsNullOrWhiteSpace(input.Name))
        {
            throw new BusinessRuleException("اسم الحساب مطلوب.");
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record AccountInput(string Code, string Name, AccountType Type, long? ParentId, string? Description, bool IsActive);

public sealed class AccountRow
{
    public long Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public AccountType Type { get; set; }
    public NormalBalance NormalBalance { get; set; }
    public long? ParentId { get; set; }
    public string? ParentCode { get; set; }
    public string? Description { get; set; }
    public bool IsSystem { get; set; }
    public bool IsActive { get; set; }
    public Money Balance { get; set; }
    public long LineCount { get; set; }

    public string Label => $"{Code} - {Name}";
}

public sealed class JournalEntryRow
{
    public long Id { get; set; }
    public string EntryNumber { get; set; } = "";
    public DateOnly EntryDate { get; set; }
    public string Memo { get; set; } = "";
    public JournalSource Source { get; set; }
    public Money TotalDebit { get; set; }
    public DateTime? PostedAt { get; set; }
    public bool IsReversal { get; set; }
    public long? ReversesJournalEntryId { get; set; }
    public string? CreatedByName { get; set; }
    public string? ReversedByNumber { get; set; }

    public bool CanBeReversedByHand => (Source is JournalSource.Manual or JournalSource.Opening) && !IsReversal && ReversedByNumber is null;
}

public sealed class JournalLineRow
{
    public int LineNumber { get; set; }
    public string AccountCode { get; set; } = "";
    public string AccountName { get; set; } = "";
    public Money Debit { get; set; }
    public Money Credit { get; set; }
    public string? Memo { get; set; }
}
