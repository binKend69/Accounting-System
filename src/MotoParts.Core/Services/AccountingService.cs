using MotoParts.Core.Common;
using MotoParts.Core.Data;
using MotoParts.Core.Domain;
using MotoParts.Core.Security;

namespace MotoParts.Core.Services;

/// <summary>
/// The only code in the system that writes journal entries and lines.
///
/// Sales, purchases, refunds, vouchers, payroll and manual entries all come
/// through <see cref="Post"/> or <see cref="Reverse"/>; none of them assemble
/// ledger rows themselves. Every post runs as:
///
///   allocate number -> insert draft -> insert lines -> seal (set posted_at)
///
/// The sealing UPDATE is where the database re-adds the lines and refuses an
/// entry that does not balance, so nothing unbalanced can ever be committed,
/// whatever a caller passed in.
/// </summary>
public sealed class AccountingService
{
    private readonly DbSession _db;
    private readonly IClock _clock;
    private readonly SequenceService _sequences;
    private readonly AuditService _audit;
    private readonly Dictionary<string, Account> _accountsByCode = new(StringComparer.Ordinal);

    public AccountingService(DbSession db, IClock clock, SequenceService sequences, AuditService audit)
    {
        _db = db;
        _clock = clock;
        _sequences = sequences;
        _audit = audit;
    }

    /// <summary>
    /// Posts a balanced journal entry. Joins the caller's transaction, so a
    /// sale that fails after posting takes its entry down with it.
    /// </summary>
    public JournalEntry Post(
        string memo,
        IReadOnlyList<LedgerLine> lines,
        JournalSource source,
        DateOnly entryDate,
        Actor? actor,
        string? sourceDocumentType = null,
        long? sourceDocumentId = null,
        long? reversesEntryId = null)
    {
        if (string.IsNullOrWhiteSpace(memo))
        {
            throw new BusinessRuleException("بيان القيد مطلوب.");
        }

        if (lines.Count < 2)
        {
            throw new BusinessRuleException("القيد المزدوج يحتاج سطراً مديناً وسطراً دائناً على الأقل.");
        }

        // Fail fast with a readable message. The trigger is the real guarantee.
        AssertBalanced(lines);

        var period = ResolveOpenPeriodFor(entryDate);

        return _db.InTransaction(() =>
        {
            var number = _sequences.Next(SequenceService.JournalEntry, entryDate.Year);

            var entryId = _db.Insert(
                """
                INSERT INTO journal_entries
                    (entry_number, fiscal_period_id, entry_date, memo, source,
                     source_document_type, source_document_id,
                     reverses_journal_entry_id, is_reversal, created_by, created_at, updated_at)
                VALUES
                    (@number, @periodId, @entryDate, @memo, @source,
                     @sourceDocumentType, @sourceDocumentId,
                     @reversesEntryId, @isReversal, @createdBy, @now, @now)
                RETURNING id
                """,
                new
                {
                    number,
                    periodId = period.Id,
                    entryDate,
                    memo = memo.Trim(),
                    source = source.ToString(),
                    sourceDocumentType,
                    sourceDocumentId,
                    reversesEntryId,
                    isReversal = reversesEntryId is not null,
                    createdBy = actor?.Id,
                    now = _clock.Now,
                });

            var lineNumber = 0;

            foreach (var line in lines)
            {
                _db.Execute(
                    """
                    INSERT INTO journal_lines (journal_entry_id, account_id, line_number, debit, credit, memo)
                    VALUES (@entryId, @accountId, @lineNumber, @debit, @credit, @memo)
                    """,
                    new
                    {
                        entryId,
                        accountId = ResolveAccountId(line),
                        lineNumber = ++lineNumber,
                        debit = line.DebitAmount,
                        credit = line.CreditAmount,
                        memo = line.Memo,
                    });
            }

            // Sealing. The trigger re-derives the totals from the lines and
            // refuses unless they balance and match.
            _db.Execute(
                "UPDATE journal_entries SET posted_at = @now, updated_at = @now WHERE id = @entryId",
                new { entryId, now = _clock.Now });

            var entry = GetEntry(entryId);

            _audit.Record(nameof(JournalEntry), entry.Id, AuditAction.Posted, actor, newValues: new
            {
                entry.EntryNumber,
                entry.EntryDate,
                entry.Memo,
                entry.Source,
                entry.TotalDebit,
                entry.TotalCredit,
                ReversesEntryId = reversesEntryId,
            });

            return entry;
        });
    }

    /// <summary>
    /// Posts the mirror image of a posted entry. The original is never touched:
    /// both stay on the ledger and the pair nets to zero. The schema allows an
    /// entry to be reversed only once.
    /// </summary>
    public JournalEntry Reverse(
        long originalEntryId,
        DateOnly entryDate,
        Actor? actor,
        string? memo = null,
        JournalSource? source = null,
        string? sourceDocumentType = null,
        long? sourceDocumentId = null)
    {
        var original = GetEntry(originalEntryId);

        if (!original.IsPosted)
        {
            throw new BusinessRuleException("لا يمكن عكس قيد غير مرحّل.");
        }

        if (IsReversed(original.Id))
        {
            throw new BusinessRuleException($"القيد {original.EntryNumber} تم عكسه من قبل.");
        }

        var lines = GetLines(original.Id)
            .Select(l => l.IsDebit
                ? LedgerLine.Credit(l.AccountId, l.Debit, l.Memo)
                : LedgerLine.Debit(l.AccountId, l.Credit, l.Memo))
            .ToList();

        return Post(
            memo ?? $"عكس القيد {original.EntryNumber}: {original.Memo}",
            lines,
            source ?? original.Source,
            entryDate,
            actor,
            sourceDocumentType,
            sourceDocumentId,
            reversesEntryId: original.Id);
    }

    /// <summary>A manual entry, typed in by an accountant. Owner-level permission.</summary>
    public JournalEntry PostManual(Actor actor, DateOnly entryDate, string memo, IReadOnlyList<LedgerLine> lines, bool isOpeningBalance = false)
    {
        actor.Require(Permission.AccountingPostManualEntry, "ليست لديك صلاحية تسجيل قيود يدوية.");

        foreach (var line in lines)
        {
            var account = line.AccountId is { } id ? GetAccountById(id) : Account(line.AccountCode!);

            if (!account.IsActive)
            {
                throw new BusinessRuleException($"الحساب {account.Label} موقوف ولا يقبل قيوداً جديدة.");
            }
        }

        return Post(memo, lines, isOpeningBalance ? JournalSource.Opening : JournalSource.Manual, entryDate, actor);
    }

    /// <summary>
    /// Reverses a manual or opening entry on an operator's request.
    ///
    /// Entries produced by a document (a sale, a voucher, a payroll run) are
    /// corrected through that document - a refund, a void - never here, or the
    /// document and the ledger would disagree about what happened.
    /// </summary>
    public JournalEntry ReverseManual(Actor actor, long entryId, DateOnly entryDate, string? memo)
    {
        actor.Require(Permission.AccountingReverseEntry, "ليست لديك صلاحية عكس القيود.");

        var entry = GetEntry(entryId);

        if (entry.Source is not (JournalSource.Manual or JournalSource.Opening) || entry.IsReversal)
        {
            throw new BusinessRuleException(
                "هذا القيد ناتج عن مستند (فاتورة أو سند أو مسير رواتب). صحّحه من المستند نفسه بالاسترجاع أو الإلغاء.");
        }

        return Reverse(entryId, entryDate, actor, string.IsNullOrWhiteSpace(memo) ? null : memo);
    }

    /// <summary>The fiscal period covering a date, provided it exists and is open.</summary>
    public FiscalPeriod ResolveOpenPeriodFor(DateOnly date)
    {
        var period = _db.QueryFirstOrDefault<FiscalPeriod>(
            "SELECT * FROM fiscal_periods WHERE @date BETWEEN start_date AND end_date",
            new { date });

        if (period is null)
        {
            throw ClosedFiscalPeriodException.Missing(date);
        }

        if (period.IsLocked)
        {
            throw ClosedFiscalPeriodException.Locked(date);
        }

        return period;
    }

    /// <summary>A system account by its code. Memoised for the life of the session.</summary>
    public Account Account(string code)
    {
        if (_accountsByCode.TryGetValue(code, out var cached))
        {
            return cached;
        }

        var account = _db.QueryFirstOrDefault<Account>("SELECT * FROM accounts WHERE code = @code", new { code })
            ?? throw new BusinessRuleException($"الحساب {code} غير موجود في دليل الحسابات.");

        _accountsByCode[code] = account;
        return account;
    }

    public Account GetAccountById(long id) =>
        _db.QueryFirstOrDefault<Account>("SELECT * FROM accounts WHERE id = @id", new { id })
        ?? throw new BusinessRuleException("الحساب غير موجود.");

    public JournalEntry GetEntry(long id) =>
        _db.QueryFirstOrDefault<JournalEntry>("SELECT * FROM journal_entries WHERE id = @id", new { id })
        ?? throw new BusinessRuleException("القيد غير موجود.");

    public IReadOnlyList<JournalLine> GetLines(long entryId) =>
        _db.Query<JournalLine>(
            "SELECT * FROM journal_lines WHERE journal_entry_id = @entryId ORDER BY line_number",
            new { entryId }).ToList();

    public bool IsReversed(long entryId) =>
        _db.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM journal_entries WHERE reverses_journal_entry_id = @entryId",
            new { entryId }) > 0;

    /// <summary>
    /// Signed balance in the account's own normal direction, from posted entries.
    /// A debit-normal account with more credits than debits returns negative.
    /// </summary>
    public Money Balance(long accountId, DateOnly? upTo = null, DateOnly? from = null)
    {
        var account = GetAccountById(accountId);

        var totals = _db.QuerySingle<(long Debit, long Credit)>(
            """
            SELECT COALESCE(SUM(l.debit), 0), COALESCE(SUM(l.credit), 0)
            FROM journal_lines l
            JOIN journal_entries e ON e.id = l.journal_entry_id
            WHERE l.account_id = @accountId
              AND e.posted_at IS NOT NULL
              AND (@from IS NULL OR e.entry_date >= @from)
              AND (@upTo IS NULL OR e.entry_date <= @upTo)
            """,
            new { accountId, from, upTo });

        var debit = Money.FromMinorUnits(totals.Debit);
        var credit = Money.FromMinorUnits(totals.Credit);

        return account.NormalBalance == NormalBalance.Debit ? debit - credit : credit - debit;
    }

    public Money BalanceByCode(string code, DateOnly? upTo = null) => Balance(Account(code).Id, upTo);

    private long ResolveAccountId(LedgerLine line) =>
        line.AccountId ?? Account(line.AccountCode!).Id;

    private static void AssertBalanced(IReadOnlyList<LedgerLine> lines)
    {
        var debit = lines.Sum(l => l.DebitAmount);
        var credit = lines.Sum(l => l.CreditAmount);

        if (debit != credit)
        {
            throw new BusinessRuleException(
                $"القيد غير متوازن: المدين {debit.ToDisplay()} والدائن {credit.ToDisplay()}، والفرق {(debit - credit).Abs().ToDisplay()}.");
        }

        if (debit.IsZero)
        {
            throw new BusinessRuleException("لا يمكن تسجيل قيد بمبلغ صفر.");
        }
    }
}
