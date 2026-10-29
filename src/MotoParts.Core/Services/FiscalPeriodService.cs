using System.Globalization;
using MotoParts.Core.Common;
using MotoParts.Core.Data;
using MotoParts.Core.Domain;
using MotoParts.Core.Security;

namespace MotoParts.Core.Services;

/// <summary>
/// Opening, closing and reopening accounting periods.
///
/// Closing a period is what makes past figures defensible: the triggers then
/// refuse every write inside it. Amending a closed period is done by an explicit,
/// reasoned, audited reopen - never by bypassing the triggers - so there is no
/// code path anywhere that writes into a locked period, and the reopening itself
/// is on the record.
/// </summary>
public sealed class FiscalPeriodService
{
    private readonly DbSession _db;
    private readonly IClock _clock;
    private readonly AuditService _audit;

    public FiscalPeriodService(DbSession db, IClock clock, AuditService audit)
    {
        _db = db;
        _clock = clock;
        _audit = audit;
    }

    public IReadOnlyList<FiscalPeriod> All() =>
        _db.Query<FiscalPeriod>("SELECT * FROM fiscal_periods ORDER BY start_date DESC").ToList();

    public FiscalPeriod Get(long id) =>
        _db.QueryFirstOrDefault<FiscalPeriod>("SELECT * FROM fiscal_periods WHERE id = @id", new { id })
        ?? throw new BusinessRuleException("الفترة المحاسبية غير موجودة.");

    public FiscalPeriod? Containing(DateOnly date) =>
        _db.QueryFirstOrDefault<FiscalPeriod>(
            "SELECT * FROM fiscal_periods WHERE @date BETWEEN start_date AND end_date", new { date });

    public FiscalPeriod Create(string name, DateOnly start, DateOnly end, Actor? actor)
    {
        actor?.Require(Permission.FiscalPeriodManage);

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new BusinessRuleException("اسم الفترة مطلوب.");
        }

        if (start > end)
        {
            throw new BusinessRuleException("لا يمكن أن تنتهي الفترة المحاسبية قبل بدايتها.");
        }

        var overlap = _db.QueryFirstOrDefault<FiscalPeriod>(
            "SELECT * FROM fiscal_periods WHERE start_date <= @end AND end_date >= @start",
            new { start, end });

        if (overlap is not null)
        {
            throw new BusinessRuleException(
                $"هذه الفترة تتداخل مع الفترة {overlap.Name} ({overlap.StartDate:yyyy-MM-dd} إلى {overlap.EndDate:yyyy-MM-dd}).");
        }

        return _db.InTransaction(() =>
        {
            var id = _db.Insert(
                """
                INSERT INTO fiscal_periods (name, start_date, end_date, is_locked, created_at, updated_at)
                VALUES (@name, @start, @end, 0, @now, @now)
                RETURNING id
                """,
                new { name = name.Trim(), start, end, now = _clock.Now });

            _audit.Record(nameof(FiscalPeriod), id, AuditAction.Created, actor, newValues: new { name, start, end });

            return Get(id);
        });
    }

    /// <summary>Creates the twelve monthly periods of a year, skipping any that already exist.</summary>
    public int CreateMonthsForYear(int year, Actor? actor)
    {
        actor?.Require(Permission.FiscalPeriodManage);

        return _db.InTransaction(() =>
        {
            var created = 0;

            for (var month = 1; month <= 12; month++)
            {
                var start = new DateOnly(year, month, 1);
                var end = start.AddMonths(1).AddDays(-1);

                var exists = _db.ExecuteScalar<long>(
                    "SELECT COUNT(*) FROM fiscal_periods WHERE start_date <= @end AND end_date >= @start",
                    new { start, end }) > 0;

                if (exists)
                {
                    continue;
                }

                Create(start.ToString("yyyy-MM", CultureInfo.InvariantCulture), start, end, actor: null);
                created++;
            }

            return created;
        });
    }

    /// <summary>Closes a period. Owner only.</summary>
    public FiscalPeriod Lock(long periodId, Actor actor, string? note)
    {
        actor.Require(Permission.FiscalPeriodLock, "إقفال الفترات المحاسبية من صلاحيات المالك فقط.");

        var period = Get(periodId);

        if (period.IsLocked)
        {
            throw new BusinessRuleException($"الفترة {period.Name} مقفلة من قبل.");
        }

        var drafts = _db.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM journal_entries WHERE fiscal_period_id = @periodId AND posted_at IS NULL",
            new { periodId });

        if (drafts > 0)
        {
            throw new BusinessRuleException(
                $"في الفترة {period.Name} عدد {drafts} من القيود غير المرحّلة؛ يجب ترحيلها أو حذفها قبل الإقفال.");
        }

        return _db.InTransaction(() =>
        {
            _db.Execute(
                """
                UPDATE fiscal_periods
                SET is_locked = 1, locked_at = @now, locked_by = @actorId, lock_note = @note, updated_at = @now
                WHERE id = @periodId
                """,
                new { periodId, now = _clock.Now, actorId = actor.Id, note = Clean(note) });

            _audit.Record(nameof(FiscalPeriod), periodId, AuditAction.Locked, actor,
                new { IsLocked = false }, new { IsLocked = true }, note);

            return Get(periodId);
        });
    }

    /// <summary>Reopens a closed period. Owner only, and a reason is mandatory.</summary>
    public FiscalPeriod Unlock(long periodId, Actor actor, string reason)
    {
        actor.Require(Permission.FiscalPeriodUnlock, "إعادة فتح الفترات المحاسبية من صلاحيات المالك فقط.");

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new BusinessRuleException("إعادة فتح فترة مقفلة تتطلب ذكر السبب.");
        }

        var period = Get(periodId);

        if (!period.IsLocked)
        {
            throw new BusinessRuleException($"الفترة {period.Name} غير مقفلة.");
        }

        return _db.InTransaction(() =>
        {
            _db.Execute(
                """
                UPDATE fiscal_periods
                SET is_locked = 0, locked_at = NULL, locked_by = NULL, lock_note = @reason, updated_at = @now
                WHERE id = @periodId
                """,
                new { periodId, reason = reason.Trim(), now = _clock.Now });

            _audit.Record(nameof(FiscalPeriod), periodId, AuditAction.Unlocked, actor,
                new { IsLocked = true }, new { IsLocked = false }, reason);

            return Get(periodId);
        });
    }

    /// <summary>Renames or re-dates an empty, open period.</summary>
    public FiscalPeriod Update(long periodId, string name, DateOnly start, DateOnly end, Actor actor)
    {
        actor.Require(Permission.FiscalPeriodManage);

        var period = Get(periodId);

        if (period.IsLocked)
        {
            throw new BusinessRuleException("الفترة مقفلة؛ أعد فتحها أولاً لتعديلها.");
        }

        if (start > end)
        {
            throw new BusinessRuleException("لا يمكن أن تنتهي الفترة المحاسبية قبل بدايتها.");
        }

        return _db.InTransaction(() =>
        {
            _db.Execute(
                "UPDATE fiscal_periods SET name = @name, start_date = @start, end_date = @end, updated_at = @now WHERE id = @periodId",
                new { periodId, name = name.Trim(), start, end, now = _clock.Now });

            _audit.Record(nameof(FiscalPeriod), periodId, AuditAction.Updated, actor,
                new { period.Name, period.StartDate, period.EndDate }, new { Name = name, StartDate = start, EndDate = end });

            return Get(periodId);
        });
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
