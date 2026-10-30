using HisabTak.Domain.Accounting;

namespace HisabTak.Domain.Tests;

public sealed class CurrencyAmountTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Amount_requires_a_currency_reference(int currencyId) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new CurrencyAmount(1m, currencyId));

    [Fact]
    public void Arithmetic_keeps_exact_values_until_an_explicit_rounding_policy_is_applied()
    {
        var first = new CurrencyAmount(100.005m, 1);
        var second = new CurrencyAmount(0.004m, 1);
        Assert.Equal(new CurrencyAmount(100.009m, 1), first.Add(second));
        Assert.Equal(new CurrencyAmount(100.001m, 1), first.Subtract(second));
        Assert.Equal(100.005m, first.Amount);
    }

    [Fact]
    public void Equal_nominal_values_in_different_currencies_cannot_be_added_or_subtracted()
    {
        var first = new CurrencyAmount(100m, 1);
        var second = new CurrencyAmount(100m, 2);
        Assert.NotEqual(first, second);
        Assert.Throws<InvalidOperationException>(() => first.Add(second));
        Assert.Throws<InvalidOperationException>(() => first.Subtract(second));
    }

    [Fact]
    public void Reversal_preserves_currency_and_returns_the_original_when_reversed_twice()
    {
        var original = new CurrencyAmount(123.456m, 3);
        Assert.Equal(new CurrencyAmount(-123.456m, 3), original.Negate());
        Assert.Equal(original, original.Negate().Negate());
        Assert.Equal(new CurrencyAmount(0m, 3), original.Add(original.Negate()));
    }

    [Fact]
    public void Decimal_overflow_is_rejected_instead_of_changing_the_balance()
    {
        Assert.Throws<OverflowException>(() => new CurrencyAmount(decimal.MaxValue, 1).Add(new CurrencyAmount(1m, 1)));
        Assert.Throws<OverflowException>(() => new CurrencyAmount(decimal.MinValue, 1).Subtract(new CurrencyAmount(1m, 1)));
    }

    [Fact]
    public void Missing_operand_is_rejected()
    {
        var amount = new CurrencyAmount(1m, 1);
        Assert.Throws<ArgumentNullException>(() => amount.Add(null!));
        Assert.Throws<ArgumentNullException>(() => amount.Subtract(null!));
    }

    [Theory]
    [InlineData(100005, 10001)]
    [InlineData(-100005, -10001)]
    [InlineData(100004, 10000)]
    [InlineData(100006, 10001)]
    [InlineData(0, 0)]
    public void Explicit_rounding_uses_two_places_and_half_away_from_zero(int thousandths, int hundredths)
    {
        var original = new CurrencyAmount(thousandths / 1000m, 3);
        var rounded = original.Round(2);
        Assert.Equal(new CurrencyAmount(hundredths / 100m, 3), rounded);
        Assert.Equal(thousandths / 1000m, original.Amount);
        Assert.Equal(rounded, rounded.Round(2));
        Assert.Equal(rounded.Negate(), original.Negate().Round(2));
    }

    [Fact]
    public void Precision_is_explicit_for_additional_currencies()
    {
        var amount = new CurrencyAmount(1.2345m, 4);
        Assert.Equal(new CurrencyAmount(1m, 4), amount.Round(0));
        Assert.Equal(new CurrencyAmount(1.235m, 4), amount.Round(3));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(29)]
    public void Precision_outside_decimal_limits_is_rejected(int places) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new CurrencyAmount(1m, 1).Round(places));
}
