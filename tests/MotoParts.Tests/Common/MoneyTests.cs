using MotoParts.Core.Common;

namespace MotoParts.Tests.Common;

public sealed class MoneyTests
{
    [Theory]
    [InlineData("0.125", "0.13")]
    [InlineData("0.124", "0.12")]
    [InlineData("-0.125", "-0.13")]
    [InlineData("2.675", "2.68")]
    public void Rounds_half_away_from_zero(string input, string expected)
    {
        Assert.Equal(expected, Money.Of(decimal.Parse(input, System.Globalization.CultureInfo.InvariantCulture)).ToString());
    }

    [Fact]
    public void Sums_without_binary_drift()
    {
        var total = Enumerable.Repeat(Money.Of(0.1m), 10).Sum();

        Assert.Equal(Money.Of(1m), total);
        Assert.Equal(Money.Of(0.3m), Money.Of(0.1m) + Money.Of(0.2m));
    }

    [Fact]
    public void Multiplies_by_quantity_and_rate_with_one_final_rounding()
    {
        Assert.Equal(Money.Of(22.5m), Money.Of(9m) * Quantity.Of(2.5m));
        Assert.Equal(Money.Of(15.0m), Money.Of(100m) * 0.15m);
        Assert.Equal(Money.Of(0.33m), Money.Of(1m) * (1m / 3m));
    }

    [Fact]
    public void Round_trips_through_minor_units()
    {
        var money = Money.Of(1234.56m);

        Assert.Equal(123456L, money.MinorUnits);
        Assert.Equal(money, Money.FromMinorUnits(123456L));
        Assert.Equal(-50L, Money.Of(-0.5m).MinorUnits);
    }

    [Theory]
    [InlineData("1,234.50", "1234.50")]
    [InlineData(" 7 ", "7.00")]
    public void Parses_operator_input(string input, string expected)
    {
        Assert.True(Money.TryParse(input, out var money));
        Assert.Equal(expected, money.ToString());
    }

    [Fact]
    public void Rejects_input_that_is_not_a_number()
    {
        Assert.False(Money.TryParse("abc", out _));
        Assert.False(Money.TryParse("", out _));
    }

    [Fact]
    public void Quantities_keep_three_decimals()
    {
        Assert.Equal(1500L, Quantity.Of(1.5m).Thousandths);
        Assert.Equal("2.5", Quantity.Of(2.5m).ToString());
        Assert.Equal(Quantity.Of(0.001m), Quantity.Of(0.0005m));
    }

    [Fact]
    public void Displays_with_thousands_separators_regardless_of_locale()
    {
        Assert.Equal("1,234,567.80", Money.Of(1234567.8m).ToDisplay());
    }
}
