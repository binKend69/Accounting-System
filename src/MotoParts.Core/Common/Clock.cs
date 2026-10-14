namespace MotoParts.Core.Common;

/// <summary>
/// The source of "now" for every business timestamp and date.
///
/// Injected rather than read from <see cref="DateTime.Now"/> directly so tests
/// can pin time, and so a sale rung up at 00:30 is dated by the shop's own
/// calendar. The system runs on one machine in one shop, so business dates use
/// local time: a UTC date would put late-evening sales on the wrong day.
/// </summary>
public interface IClock
{
    /// <summary>Local time, truncated to whole seconds so stored text compares cleanly.</summary>
    DateTime Now { get; }

    DateOnly Today { get; }
}

public sealed class SystemClock : IClock
{
    public DateTime Now
    {
        get
        {
            var now = DateTime.Now;
            return new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, DateTimeKind.Local);
        }
    }

    public DateOnly Today => DateOnly.FromDateTime(DateTime.Now);
}

/// <summary>A clock that only moves when told to. For tests and seeders.</summary>
public sealed class FixedClock : IClock
{
    public FixedClock(DateTime now) => Now = now;

    public DateTime Now { get; private set; }

    public DateOnly Today => DateOnly.FromDateTime(Now);

    public void Set(DateTime now) => Now = now;

    public void Advance(TimeSpan by) => Now = Now.Add(by);
}
