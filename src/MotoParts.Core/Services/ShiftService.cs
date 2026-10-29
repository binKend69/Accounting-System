using MotoParts.Core.Common;
using MotoParts.Core.Data;
using MotoParts.Core.Domain;
using MotoParts.Core.Security;

namespace MotoParts.Core.Services;

/// <summary>
/// Opening and closing tills, and clocking staff in and out.
///
/// Shift sessions make cash someone's responsibility. Every sale, refund and
/// drawer voucher is tagged with its session, so at close:
///
///   expected   = opening float + cash sales - cash refunds
///                + cash received into the drawer - cash paid out of it
///   difference = counted - expected        (negative = drawer short)
///
/// and the difference is attributed to the person on duty. Any difference is
/// also posted to Cash Over and Short, so the Cash account keeps matching the
/// money that physically exists.
/// </summary>
public sealed class ShiftService
{
    private readonly DbSession _db;
    private readonly IClock _clock;
    private readonly AccountingService _accounting;
    private readonly AuditService _audit;

    public ShiftService(DbSession db, IClock clock, AccountingService accounting, AuditService audit)
    {
        _db = db;
        _clock = clock;
        _accounting = accounting;
        _audit = audit;
    }

    // --- Shift templates --------------------------------------------------------

    public IReadOnlyList<Shift> Shifts(bool activeOnly = false) =>
        _db.Query<Shift>("SELECT * FROM shifts WHERE (@activeOnly = 0 OR is_active = 1) ORDER BY start_time",
            new { activeOnly }).ToList();

    public Shift SaveShift(long? id, string name, TimeOnly start, TimeOnly end, bool isActive, Actor actor)
    {
        actor.Require(Permission.ShiftManageTemplates, "ليست لديك صلاحية إدارة الورديات.");

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new BusinessRuleException("اسم الوردية مطلوب.");
        }

        return _db.InTransaction(() =>
        {
            if (_db.ExecuteScalar<long>("SELECT COUNT(*) FROM shifts WHERE name = @name AND id IS NOT @id",
                    new { name = name.Trim(), id }) > 0)
            {
                throw new BusinessRuleException($"توجد وردية بالاسم \"{name.Trim()}\" من قبل.");
            }

            var shiftId = id ?? _db.Insert(
                "INSERT INTO shifts (name, start_time, end_time, is_active) VALUES (@name, @start, @end, @isActive) RETURNING id",
                new { name = name.Trim(), start, end, isActive });

            if (id is not null)
            {
                _db.Execute(
                    "UPDATE shifts SET name = @name, start_time = @start, end_time = @end, is_active = @isActive, updated_at = @now WHERE id = @id",
                    new { id, name = name.Trim(), start, end, isActive, now = _clock.Now });
            }

            return _db.QuerySingle<Shift>("SELECT * FROM shifts WHERE id = @shiftId", new { shiftId });
        });
    }

    // --- Sessions ---------------------------------------------------------------

    public ShiftSession Get(long sessionId) =>
        _db.QueryFirstOrDefault<ShiftSession>("SELECT * FROM shift_sessions WHERE id = @sessionId", new { sessionId })
        ?? throw new BusinessRuleException("الوردية غير موجودة.");

    public ShiftSession? OpenSessionFor(long userId) =>
        _db.QueryFirstOrDefault<ShiftSession>(
            "SELECT * FROM shift_sessions WHERE user_id = @userId AND status = 'Open'", new { userId });

    /// <summary>The session a sale or refund must be attached to.</summary>
    public ShiftSession RequireOpenSessionFor(long userId) =>
        OpenSessionFor(userId) ?? throw new NoOpenShiftException();

    /// <summary>Opens a till with a counted float.</summary>
    public ShiftSession Open(Actor actor, long shiftId, Money openingCash, string? notes = null)
    {
        actor.Require(Permission.ShiftManageOwn, "ليست لديك صلاحية فتح وردية.");

        if (openingCash.IsNegative)
        {
            throw new BusinessRuleException("مبلغ العهدة الافتتاحية لا يمكن أن يكون سالباً.");
        }

        return _db.InTransaction(() =>
        {
            var shift = _db.QueryFirstOrDefault<Shift>("SELECT * FROM shifts WHERE id = @shiftId", new { shiftId })
                ?? throw new BusinessRuleException("الوردية غير موجودة.");

            if (!shift.IsActive)
            {
                throw new BusinessRuleException($"الوردية {shift.Name} موقوفة.");
            }

            var active = _db.ExecuteScalar<long>("SELECT is_active FROM users WHERE id = @id", new { id = actor.Id });

            if (active != 1)
            {
                throw new BusinessRuleException("حسابك موقوف ولا يمكنه فتح وردية.");
            }

            // A readable message; the partial unique index is the guarantee.
            if (OpenSessionFor(actor.Id) is not null)
            {
                throw new BusinessRuleException("لديك وردية مفتوحة بالفعل. أغلقها قبل فتح وردية جديدة.");
            }

            var id = _db.Insert(
                """
                INSERT INTO shift_sessions (shift_id, user_id, opened_at, opening_cash, status, notes, created_at, updated_at)
                VALUES (@shiftId, @userId, @now, @openingCash, 'Open', @notes, @now, @now)
                RETURNING id
                """,
                new { shiftId, userId = actor.Id, now = _clock.Now, openingCash, notes = Clean(notes) });

            _audit.Record(nameof(ShiftSession), id, AuditAction.Opened, actor,
                newValues: new { Shift = shift.Name, OpeningCash = openingCash });

            return Get(id);
        });
    }

    /// <summary>The live drawer position of a session.</summary>
    public DrawerTotals DrawerTotals(long sessionId)
    {
        var session = Get(sessionId);

        if (!session.IsOpen)
        {
            return new DrawerTotals(
                session.OpeningCash,
                session.CashSalesTotal!.Value,
                session.CashRefundsTotal!.Value,
                session.CashReceiptsTotal!.Value,
                session.CashPayoutsTotal!.Value);
        }

        return LiveTotals(session);
    }

    /// <summary>The session holder closes their own till against a counted drawer.</summary>
    public ShiftSession Close(Actor actor, long sessionId, Money countedCash, string? notes = null)
    {
        if (countedCash.IsNegative)
        {
            throw new BusinessRuleException("المبلغ المعدود لا يمكن أن يكون سالباً.");
        }

        return _db.InTransaction(() =>
        {
            var session = Get(sessionId);

            if (session.UserId != actor.Id)
            {
                throw new AccessDeniedException("لا يمكنك إغلاق وردية موظف آخر إلا بالإغلاق الإجباري من المالك.");
            }

            if (!session.IsOpen)
            {
                throw new BusinessRuleException("هذه الوردية مغلقة من قبل.");
            }

            return CloseInternal(session, countedCash, ShiftSessionStatus.Closed, actor, notes ?? session.Notes,
                overrideReason: null, AuditAction.Closed);
        });
    }

    /// <summary>
    /// Owner override for a till someone walked away from. A reason is
    /// mandatory and lands in the audit trail with the owner's name.
    /// </summary>
    public ShiftSession ForceClose(Actor owner, long sessionId, Money countedCash, string reason)
    {
        owner.Require(Permission.ShiftForceClose, "الإغلاق الإجباري للورديات من صلاحيات المالك فقط.");

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new BusinessRuleException("الإغلاق الإجباري يتطلب ذكر السبب.");
        }

        if (countedCash.IsNegative)
        {
            throw new BusinessRuleException("المبلغ المعدود لا يمكن أن يكون سالباً.");
        }

        return _db.InTransaction(() =>
        {
            var session = Get(sessionId);

            if (!session.IsOpen)
            {
                throw new BusinessRuleException("هذه الوردية مغلقة من قبل.");
            }

            return CloseInternal(session, countedCash, ShiftSessionStatus.ForceClosed, owner, session.Notes,
                reason.Trim(), AuditAction.ForceClosed);
        });
    }

    /// <summary>
    /// Owner correction of a closed session's count, e.g. a miscounted drawer.
    /// The expected figure stands; only the change in the difference is posted,
    /// as a further adjusting entry. The original entry is never touched.
    /// </summary>
    public ShiftSession AmendClosingCash(Actor owner, long sessionId, Money correctedCash, string reason)
    {
        owner.Require(Permission.ShiftForceClose, "تصحيح عدّ الصندوق من صلاحيات المالك فقط.");

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new BusinessRuleException("تصحيح عدّ الصندوق يتطلب ذكر السبب.");
        }

        if (correctedCash.IsNegative)
        {
            throw new BusinessRuleException("المبلغ المعدود لا يمكن أن يكون سالباً.");
        }

        return _db.InTransaction(() =>
        {
            var session = Get(sessionId);

            if (session.IsOpen)
            {
                throw new BusinessRuleException("هذه الوردية ما زالت مفتوحة؛ أغلقها بدلاً من تصحيحها.");
            }

            var previousDifference = session.CashDifference!.Value;
            var newDifference = correctedCash - session.ExpectedCash!.Value;
            var delta = newDifference - previousDifference;

            _db.Execute(
                """
                UPDATE shift_sessions
                SET actual_closing_cash = @correctedCash, cash_difference = @newDifference,
                    override_reason = @reason, closed_by = @ownerId, updated_at = @now
                WHERE id = @sessionId
                """,
                new { sessionId, correctedCash, newDifference, reason = reason.Trim(), ownerId = owner.Id, now = _clock.Now });

            PostCashDifference(session, delta, owner, _clock.Today, $"تصحيح عدّ الصندوق للوردية رقم {session.Id}");

            _audit.Record(nameof(ShiftSession), sessionId, AuditAction.Amended, owner,
                new { ActualClosingCash = session.ActualClosingCash, CashDifference = previousDifference },
                new { ActualClosingCash = correctedCash, CashDifference = newDifference },
                reason);

            return Get(sessionId);
        });
    }

    public IReadOnlyList<ShiftSessionRow> Sessions(DateOnly? from, DateOnly? to, long? userId = null, bool differencesOnly = false, ShiftSessionStatus? status = null) =>
        _db.Query<ShiftSessionRow>(
            """
            SELECT s.*, u.full_name AS employee_name, sh.name AS shift_name, cb.full_name AS closed_by_name
            FROM shift_sessions s
            JOIN users u ON u.id = s.user_id
            JOIN shifts sh ON sh.id = s.shift_id
            LEFT JOIN users cb ON cb.id = s.closed_by
            WHERE (@from IS NULL OR date(s.opened_at) >= @from)
              AND (@to IS NULL OR date(s.opened_at) <= @to)
              AND (@userId IS NULL OR s.user_id = @userId)
              AND (@differencesOnly = 0 OR (s.cash_difference IS NOT NULL AND s.cash_difference <> 0))
              AND (@status IS NULL OR s.status = @status)
            ORDER BY s.opened_at DESC
            """,
            new { from, to, userId, differencesOnly, status = status?.ToString() }).ToList();

    // --- Attendance -------------------------------------------------------------

    /// <summary>Clock in for the day. Idempotent: a second call returns the same row.</summary>
    public Attendance CheckIn(Actor actor, string? notes = null)
    {
        actor.Require(Permission.AttendanceRecordOwn);
        var today = _clock.Today;

        return _db.InTransaction(() =>
        {
            var existing = _db.QueryFirstOrDefault<Attendance>(
                "SELECT * FROM attendances WHERE user_id = @userId AND work_date = @today",
                new { userId = actor.Id, today });

            if (existing is not null)
            {
                return existing;
            }

            var id = _db.Insert(
                """
                INSERT INTO attendances (user_id, work_date, check_in_at, notes, created_at, updated_at)
                VALUES (@userId, @today, @now, @notes, @now, @now) RETURNING id
                """,
                new { userId = actor.Id, today, now = _clock.Now, notes = Clean(notes) });

            return _db.QuerySingle<Attendance>("SELECT * FROM attendances WHERE id = @id", new { id });
        });
    }

    public Attendance CheckOut(Actor actor, string? notes = null)
    {
        actor.Require(Permission.AttendanceRecordOwn);

        return _db.InTransaction(() =>
        {
            var attendance = TodayFor(actor.Id);

            if (attendance?.CheckInAt is null)
            {
                throw new BusinessRuleException("لا يوجد تسجيل حضور لك اليوم.");
            }

            if (attendance.CheckOutAt is not null)
            {
                throw new BusinessRuleException("سجلت انصرافك اليوم من قبل.");
            }

            _db.Execute(
                "UPDATE attendances SET check_out_at = @now, notes = COALESCE(@notes, notes), updated_at = @now WHERE id = @id",
                new { id = attendance.Id, now = _clock.Now, notes = Clean(notes) });

            return _db.QuerySingle<Attendance>("SELECT * FROM attendances WHERE id = @id", new { id = attendance.Id });
        });
    }

    public Attendance? TodayFor(long userId) =>
        _db.QueryFirstOrDefault<Attendance>(
            "SELECT * FROM attendances WHERE user_id = @userId AND work_date = @today",
            new { userId, today = _clock.Today });

    public IReadOnlyList<AttendanceRow> Attendance(DateOnly? from, DateOnly? to, long? userId) =>
        _db.Query<AttendanceRow>(
            """
            SELECT a.*, u.full_name AS employee_name
            FROM attendances a JOIN users u ON u.id = a.user_id
            WHERE (@from IS NULL OR a.work_date >= @from)
              AND (@to IS NULL OR a.work_date <= @to)
              AND (@userId IS NULL OR a.user_id = @userId)
            ORDER BY a.work_date DESC, u.full_name
            """,
            new { from, to, userId }).ToList();

    /// <summary>A manager's correction of an attendance record, audited.</summary>
    public void CorrectAttendance(Actor actor, long attendanceId, DateTime? checkIn, DateTime? checkOut, string reason)
    {
        actor.Require(Permission.AttendanceViewAll, "ليست لديك صلاحية تعديل سجلات الحضور.");

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new BusinessRuleException("تعديل سجل الحضور يتطلب ذكر السبب.");
        }

        if (checkIn is not null && checkOut is not null && checkOut < checkIn)
        {
            throw new BusinessRuleException("وقت الانصراف لا يمكن أن يسبق وقت الحضور.");
        }

        _db.InTransaction(() =>
        {
            var before = _db.QuerySingle<Attendance>("SELECT * FROM attendances WHERE id = @attendanceId", new { attendanceId });

            _db.Execute(
                "UPDATE attendances SET check_in_at = @checkIn, check_out_at = @checkOut, updated_at = @now WHERE id = @attendanceId",
                new { attendanceId, checkIn, checkOut, now = _clock.Now });

            _audit.Record(nameof(Domain.Attendance), attendanceId, AuditAction.Updated, actor,
                new { before.CheckInAt, before.CheckOutAt }, new { CheckInAt = checkIn, CheckOutAt = checkOut }, reason);
        });
    }

    // --- Internals --------------------------------------------------------------

    private DrawerTotals LiveTotals(ShiftSession session)
    {
        var sessionId = session.Id;

        long Sum(string sql) => _db.ExecuteScalar<long>(sql, new { sessionId });

        return new DrawerTotals(
            session.OpeningCash,
            Money.FromMinorUnits(Sum(
                "SELECT COALESCE(SUM(total), 0) FROM sales WHERE shift_session_id = @sessionId AND payment_method = 'Cash'")),
            Money.FromMinorUnits(Sum(
                "SELECT COALESCE(SUM(total), 0) FROM sale_refunds WHERE shift_session_id = @sessionId AND payment_method = 'Cash'")),
            Money.FromMinorUnits(Sum(
                "SELECT COALESCE(SUM(amount), 0) FROM customer_receipts WHERE shift_session_id = @sessionId AND voided_at IS NULL")),
            Money.FromMinorUnits(Sum(
                """
                SELECT COALESCE((SELECT SUM(amount) FROM supplier_payments WHERE shift_session_id = @sessionId AND voided_at IS NULL), 0)
                     + COALESCE((SELECT SUM(amount) FROM expense_vouchers WHERE shift_session_id = @sessionId AND voided_at IS NULL), 0)
                """)));
    }

    private ShiftSession CloseInternal(
        ShiftSession session,
        Money countedCash,
        ShiftSessionStatus status,
        Actor actor,
        string? notes,
        string? overrideReason,
        AuditAction action)
    {
        // Inside the write transaction: nothing can be rung up on this session
        // between reading the totals and storing them.
        var totals = LiveTotals(session);
        var difference = countedCash - totals.Expected;
        var now = _clock.Now;

        _db.Execute(
            """
            UPDATE shift_sessions SET
                closed_at = @now, status = @status,
                cash_sales_total = @CashSales, cash_refunds_total = @CashRefunds,
                cash_receipts_total = @CashReceipts, cash_payouts_total = @CashPayouts,
                expected_cash = @Expected, actual_closing_cash = @countedCash, cash_difference = @difference,
                closed_by = @actorId, override_reason = @overrideReason, notes = @notes, updated_at = @now
            WHERE id = @sessionId
            """,
            new
            {
                sessionId = session.Id,
                now,
                status = status.ToString(),
                totals.CashSales,
                totals.CashRefunds,
                totals.CashReceipts,
                totals.CashPayouts,
                totals.Expected,
                countedCash,
                difference,
                actorId = actor.Id,
                overrideReason,
                notes = Clean(notes),
            });

        PostCashDifference(session, difference, actor, DateOnly.FromDateTime(now), memo: null);

        _audit.Record(nameof(ShiftSession), session.Id, action, actor,
            new { Status = ShiftSessionStatus.Open },
            new { Status = status, Expected = totals.Expected, Counted = countedCash, Difference = difference },
            overrideReason);

        return Get(session.Id);
    }

    /// <summary>
    /// Short drawer: Dr Cash Over and Short, Cr Cash - the expense absorbs the
    /// missing money and Cash drops to what is really there. Over: the mirror.
    /// </summary>
    private void PostCashDifference(ShiftSession session, Money difference, Actor actor, DateOnly date, string? memo)
    {
        if (difference.IsZero)
        {
            return;
        }

        var amount = difference.Abs();
        var employee = _db.ExecuteScalar<string>("SELECT full_name FROM users WHERE id = @id", new { id = session.UserId });

        memo ??= difference.IsNegative
            ? $"عجز صندوق في الوردية رقم {session.Id} ({employee})"
            : $"زيادة صندوق في الوردية رقم {session.Id} ({employee})";

        IReadOnlyList<LedgerLine> lines = difference.IsNegative
            ?
            [
                LedgerLine.Debit(AccountCodes.CashOverShort, amount, "عجز الصندوق"),
                LedgerLine.Credit(AccountCodes.Cash, amount, "تخفيض النقدية للمبلغ الفعلي"),
            ]
            :
            [
                LedgerLine.Debit(AccountCodes.Cash, amount, "زيادة النقدية للمبلغ الفعلي"),
                LedgerLine.Credit(AccountCodes.CashOverShort, amount, "زيادة الصندوق"),
            ];

        _accounting.Post(memo, lines, JournalSource.CashDifference, date, actor, nameof(ShiftSession), session.Id);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record DrawerTotals(Money OpeningCash, Money CashSales, Money CashRefunds, Money CashReceipts, Money CashPayouts)
{
    public Money Expected => OpeningCash + CashSales - CashRefunds + CashReceipts - CashPayouts;
}

public sealed class ShiftSessionRow
{
    public long Id { get; set; }
    public long ShiftId { get; set; }
    public long UserId { get; set; }
    public string EmployeeName { get; set; } = "";
    public string ShiftName { get; set; } = "";
    public DateTime OpenedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public Money OpeningCash { get; set; }
    public Money? CashSalesTotal { get; set; }
    public Money? CashRefundsTotal { get; set; }
    public Money? CashReceiptsTotal { get; set; }
    public Money? CashPayoutsTotal { get; set; }
    public Money? ExpectedCash { get; set; }
    public Money? ActualClosingCash { get; set; }
    public Money? CashDifference { get; set; }
    public ShiftSessionStatus Status { get; set; }
    public string? ClosedByName { get; set; }
    public string? OverrideReason { get; set; }
    public string? Notes { get; set; }

    public bool IsShort => CashDifference is { IsNegative: true };

    public bool IsOver => CashDifference is { IsPositive: true };
}

public sealed class AttendanceRow
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public string EmployeeName { get; set; } = "";
    public DateOnly WorkDate { get; set; }
    public DateTime? CheckInAt { get; set; }
    public DateTime? CheckOutAt { get; set; }
    public string? Notes { get; set; }

    public TimeSpan? Worked => CheckInAt is { } i && CheckOutAt is { } o ? o - i : null;
}
