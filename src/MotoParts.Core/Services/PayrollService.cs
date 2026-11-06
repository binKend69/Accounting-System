using System.Globalization;
using MotoParts.Core.Common;
using MotoParts.Core.Data;
using MotoParts.Core.Domain;
using MotoParts.Core.Security;

namespace MotoParts.Core.Services;

/// <summary>
/// Monthly payroll.
///
///   Draft  - one line per active salaried employee, from their base salary;
///            additions (bonus, overtime) and deductions (absence) are edited in.
///   Post   - Dr Salaries Expense / Cr Salaries Payable: the month's cost is
///            recognised whether or not it has been paid yet.
///   Pay    - Dr Salaries Payable / Cr Cash or Bank.
///
/// One run per month is enforced by the schema, so a month cannot be paid twice.
/// </summary>
public sealed class PayrollService
{
    private readonly DbSession _db;
    private readonly IClock _clock;
    private readonly AccountingService _accounting;
    private readonly SequenceService _sequences;
    private readonly AuditService _audit;

    public PayrollService(DbSession db, IClock clock, AccountingService accounting, SequenceService sequences, AuditService audit)
    {
        _db = db;
        _clock = clock;
        _accounting = accounting;
        _sequences = sequences;
        _audit = audit;
    }

    public IReadOnlyList<PayrollRun> Runs() =>
        _db.Query<PayrollRun>("SELECT * FROM payroll_runs ORDER BY period_year DESC, period_month DESC").ToList();

    public PayrollRun Get(long id) =>
        _db.QueryFirstOrDefault<PayrollRun>("SELECT * FROM payroll_runs WHERE id = @id", new { id })
        ?? throw new BusinessRuleException("مسير الرواتب غير موجود.");

    public IReadOnlyList<PayrollLine> Lines(long runId) =>
        _db.Query<PayrollLine>("SELECT * FROM payroll_lines WHERE payroll_run_id = @runId ORDER BY employee_name", new { runId }).ToList();

    /// <summary>Drafts the month's payroll from every active employee with a salary.</summary>
    public PayrollRun Draft(Actor actor, int year, int month)
    {
        actor.Require(Permission.PayrollManage, "إدارة الرواتب من صلاحيات المالك.");

        if (month is < 1 or > 12)
        {
            throw new BusinessRuleException("الشهر غير صحيح.");
        }

        return _db.InTransaction(() =>
        {
            if (_db.ExecuteScalar<long>("SELECT COUNT(*) FROM payroll_runs WHERE period_year = @year AND period_month = @month",
                    new { year, month }) > 0)
            {
                throw new BusinessRuleException($"يوجد مسير رواتب لشهر {year:0000}-{month:00} من قبل.");
            }

            var employees = _db.Query<User>("SELECT * FROM users WHERE is_active = 1 AND base_salary > 0 ORDER BY full_name").ToList();

            if (employees.Count == 0)
            {
                throw new BusinessRuleException("لا يوجد موظفون نشطون لهم راتب أساسي. حدد الرواتب من شاشة المستخدمين أولاً.");
            }

            var runId = _db.Insert(
                """
                INSERT INTO payroll_runs (period_year, period_month, status, created_by, created_at, updated_at)
                VALUES (@year, @month, 'Draft', @actorId, @now, @now) RETURNING id
                """,
                new { year, month, actorId = actor.Id, now = _clock.Now });

            foreach (var employee in employees)
            {
                _db.Execute(
                    """
                    INSERT INTO payroll_lines (payroll_run_id, user_id, employee_name, base_salary, additions, deductions, net_pay)
                    VALUES (@runId, @userId, @name, @salary, 0, 0, @salary)
                    """,
                    new { runId, userId = employee.Id, name = employee.FullName, salary = employee.BaseSalary });
            }

            RecalculateTotals(runId);
            _audit.Record(nameof(PayrollRun), runId, AuditAction.Created, actor, newValues: new { year, month, Employees = employees.Count });

            return Get(runId);
        });
    }

    public PayrollLine UpdateLine(Actor actor, long lineId, Money additions, Money deductions, string? notes)
    {
        actor.Require(Permission.PayrollManage, "إدارة الرواتب من صلاحيات المالك.");

        if (additions.IsNegative || deductions.IsNegative)
        {
            throw new BusinessRuleException("الإضافات والخصومات لا يمكن أن تكون سالبة.");
        }

        return _db.InTransaction(() =>
        {
            var line = _db.QueryFirstOrDefault<PayrollLine>("SELECT * FROM payroll_lines WHERE id = @lineId", new { lineId })
                ?? throw new BusinessRuleException("سطر الراتب غير موجود.");

            EnsureDraft(Get(line.PayrollRunId));

            var net = line.BaseSalary + additions - deductions;

            if (net.IsNegative)
            {
                throw new BusinessRuleException($"الخصومات على {line.EmployeeName} تتجاوز مستحقاته.");
            }

            _db.Execute(
                "UPDATE payroll_lines SET additions = @additions, deductions = @deductions, net_pay = @net, notes = @notes WHERE id = @lineId",
                new { lineId, additions, deductions, net, notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim() });

            RecalculateTotals(line.PayrollRunId);

            return _db.QuerySingle<PayrollLine>("SELECT * FROM payroll_lines WHERE id = @lineId", new { lineId });
        });
    }

    public void RemoveLine(Actor actor, long lineId)
    {
        actor.Require(Permission.PayrollManage, "إدارة الرواتب من صلاحيات المالك.");

        _db.InTransaction(() =>
        {
            var line = _db.QueryFirstOrDefault<PayrollLine>("SELECT * FROM payroll_lines WHERE id = @lineId", new { lineId })
                ?? throw new BusinessRuleException("سطر الراتب غير موجود.");

            EnsureDraft(Get(line.PayrollRunId));

            _db.Execute("DELETE FROM payroll_lines WHERE id = @lineId", new { lineId });
            RecalculateTotals(line.PayrollRunId);
        });
    }

    /// <summary>Discards a draft. Posted runs are permanent.</summary>
    public void DeleteDraft(Actor actor, long runId)
    {
        actor.Require(Permission.PayrollManage, "إدارة الرواتب من صلاحيات المالك.");

        _db.InTransaction(() =>
        {
            EnsureDraft(Get(runId));
            _db.Execute("DELETE FROM payroll_runs WHERE id = @runId", new { runId });
            _audit.Record(nameof(PayrollRun), runId, AuditAction.Deleted, actor);
        });
    }

    /// <summary>
    /// Approves the run and accrues it: Dr Salaries Expense / Cr Salaries Payable,
    /// dated the last day of the month unless another date is given.
    /// </summary>
    public PayrollRun Post(Actor actor, long runId, DateOnly? postingDate = null)
    {
        actor.Require(Permission.PayrollManage, "إدارة الرواتب من صلاحيات المالك.");

        return _db.InTransaction(() =>
        {
            var run = Get(runId);
            EnsureDraft(run);

            if (!run.TotalNet.IsPositive)
            {
                throw new BusinessRuleException("صافي مسير الرواتب صفر؛ لا يوجد ما يُعتمد.");
            }

            var date = postingDate ?? new DateOnly(run.PeriodYear, run.PeriodMonth, 1).AddMonths(1).AddDays(-1);
            var number = _sequences.Next(SequenceService.Payroll, run.PeriodYear);

            var entry = _accounting.Post(
                $"مسير رواتب {run.PeriodLabel} ({number})",
                [
                    LedgerLine.Debit(AccountCodes.SalariesExpense, run.TotalNet, $"رواتب {run.PeriodLabel}"),
                    LedgerLine.Credit(AccountCodes.SalariesPayable, run.TotalNet, $"رواتب {run.PeriodLabel}"),
                ],
                JournalSource.Payroll, date, actor, nameof(PayrollRun), runId);

            _db.Execute(
                """
                UPDATE payroll_runs SET status = 'Posted', run_number = @number, posting_date = @date,
                    posted_at = @now, posted_by = @actorId, journal_entry_id = @entryId, updated_at = @now
                WHERE id = @runId
                """,
                new { runId, number, date, now = _clock.Now, actorId = actor.Id, entryId = entry.Id });

            _audit.Record(nameof(PayrollRun), runId, AuditAction.Posted, actor, newValues: new { number, run.TotalNet });

            return Get(runId);
        });
    }

    /// <summary>Pays a posted run: Dr Salaries Payable / Cr Cash or Bank.</summary>
    public PayrollRun Pay(Actor actor, long runId, PaymentMethod method, DateOnly? paymentDate = null)
    {
        actor.Require(Permission.PayrollManage, "إدارة الرواتب من صلاحيات المالك.");

        if (method == PaymentMethod.Credit)
        {
            throw new BusinessRuleException("تُصرف الرواتب نقداً أو بتحويل بنكي.");
        }

        return _db.InTransaction(() =>
        {
            var run = Get(runId);

            if (run.Status == PayrollStatus.Draft)
            {
                throw new BusinessRuleException("يجب اعتماد مسير الرواتب قبل صرفه.");
            }

            if (run.Status == PayrollStatus.Paid)
            {
                throw new BusinessRuleException("مسير الرواتب مصروف من قبل.");
            }

            var date = paymentDate ?? _clock.Today;

            var entry = _accounting.Post(
                $"صرف رواتب {run.PeriodLabel} ({run.RunNumber})",
                [
                    LedgerLine.Debit(AccountCodes.SalariesPayable, run.TotalNet, $"رواتب {run.PeriodLabel}"),
                    LedgerLine.Credit(SaleService.SettlementAccount(method), run.TotalNet, $"رواتب {run.PeriodLabel}"),
                ],
                JournalSource.PayrollPayment, date, actor, nameof(PayrollRun), runId);

            _db.Execute(
                """
                UPDATE payroll_runs SET status = 'Paid', payment_date = @date, paid_at = @now, paid_by = @actorId,
                    payment_method = @method, payment_journal_entry_id = @entryId, updated_at = @now
                WHERE id = @runId
                """,
                new { runId, date, now = _clock.Now, actorId = actor.Id, method = method.ToString(), entryId = entry.Id });

            _audit.Record(nameof(PayrollRun), runId, AuditAction.Paid, actor, newValues: new { run.TotalNet, Method = method });

            return Get(runId);
        });
    }

    public static string MonthName(int month) =>
        CultureInfo.GetCultureInfo("ar-EG").DateTimeFormat.GetMonthName(month);

    private void RecalculateTotals(long runId)
    {
        _db.Execute(
            """
            UPDATE payroll_runs SET
                total_base = COALESCE((SELECT SUM(base_salary) FROM payroll_lines WHERE payroll_run_id = @runId), 0),
                total_additions = COALESCE((SELECT SUM(additions) FROM payroll_lines WHERE payroll_run_id = @runId), 0),
                total_deductions = COALESCE((SELECT SUM(deductions) FROM payroll_lines WHERE payroll_run_id = @runId), 0),
                total_net = COALESCE((SELECT SUM(net_pay) FROM payroll_lines WHERE payroll_run_id = @runId), 0),
                updated_at = @now
            WHERE id = @runId
            """,
            new { runId, now = _clock.Now });
    }

    private static void EnsureDraft(PayrollRun run)
    {
        if (run.Status != PayrollStatus.Draft)
        {
            throw new BusinessRuleException("مسير الرواتب معتمد ولا يمكن تعديله.");
        }
    }
}
