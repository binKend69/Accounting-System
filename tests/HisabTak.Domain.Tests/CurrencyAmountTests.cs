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
}
