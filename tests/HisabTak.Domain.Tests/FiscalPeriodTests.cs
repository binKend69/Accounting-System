using HisabTak.Domain.Accounting;

namespace HisabTak.Domain.Tests;

public sealed class FiscalPeriodTests
{
    private static readonly DateOnly Start = new(2026, 1, 1);
    private static readonly DateTime UtcNow = new(2026, 10, 30, 10, 0, 0, DateTimeKind.Utc);
    private static FiscalPeriod Period() => new("يناير", Start, Start.AddDays(30));

    [Theory]
    [InlineData(0, true)]
    [InlineData(30, true)]
    [InlineData(-1, false)]
    [InlineData(31, false)]
    public void Posting_dates_include_both_boundaries_and_reject_outside_dates(int offset, bool allowed)
    {
        var period = Period();
        Assert.Equal(allowed, period.Contains(Start.AddDays(offset)));
        if (allowed) period.EnsurePostingAllowed(Start.AddDays(offset));
        else Assert.Throws<InvalidOperationException>(() => period.EnsurePostingAllowed(Start.AddDays(offset)));
    }

    [Fact]
    public void Close_records_actor_and_time_and_blocks_posting()
    {
        var period = Period();
        period.Close(3, UtcNow);
        Assert.True(period.IsClosed);
        Assert.Equal(3, period.ClosedByUserId);
        Assert.Equal(UtcNow, period.ClosedAt);
        Assert.Throws<InvalidOperationException>(() => period.EnsurePostingAllowed(Start));
        Assert.Throws<InvalidOperationException>(() => period.Close(4, UtcNow));
        Assert.Equal(3, period.ClosedByUserId);
    }

    [Fact]
    public void Reopening_clears_current_closure_and_keeps_date_boundaries()
    {
        var period = Period();
        period.Close(3, UtcNow);
        period.Reopen();
        Assert.False(period.IsClosed);
        Assert.Null(period.ClosedAt);
        Assert.Null(period.ClosedByUserId);
        period.EnsurePostingAllowed(Start);
        Assert.Throws<InvalidOperationException>(() => period.EnsurePostingAllowed(Start.AddDays(-1)));
        Assert.Throws<InvalidOperationException>(() => period.Reopen());
    }

    [Fact]
    public void Invalid_actor_or_non_utc_time_cannot_close_a_period()
    {
        var period = Period();
        Assert.Throws<ArgumentOutOfRangeException>(() => period.Close(0, UtcNow));
        Assert.Throws<ArgumentException>(() => period.Close(1, DateTime.SpecifyKind(UtcNow, DateTimeKind.Unspecified)));
        Assert.False(period.IsClosed);
        Assert.Null(period.ClosedByUserId);
    }

    [Fact]
    public void Inactive_or_deleted_periods_are_unavailable()
    {
        var inactive = Period();
        inactive.SetActive(false);
        Assert.Throws<InvalidOperationException>(() => inactive.EnsurePostingAllowed(Start));
        Assert.Throws<InvalidOperationException>(() => inactive.Close(1, UtcNow));
        var deleted = Period();
        deleted.Close(1, UtcNow);
        deleted.MarkDeleted();
        Assert.Throws<InvalidOperationException>(() => deleted.EnsurePostingAllowed(Start));
        Assert.Throws<InvalidOperationException>(() => deleted.Reopen());
    }
}
