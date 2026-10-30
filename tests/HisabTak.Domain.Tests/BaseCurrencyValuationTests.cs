using HisabTak.Domain.Accounting;

namespace HisabTak.Domain.Tests;

public sealed class BaseCurrencyValuationTests
{
    private static readonly DateOnly OperationDate = new(2026, 10, 30);

    [Fact]
    public void Approved_example_preserves_the_original_and_its_historical_base_value()
    {
        var original = new CurrencyAmount(100m, 1);
        var valuation = new BaseCurrencyValuation(original, 2, 3.8m, OperationDate);
        Assert.Equal(new CurrencyAmount(100m, 1), valuation.Original);
        Assert.Equal(new CurrencyAmount(380m, 2), valuation.ExactBaseAmount);
        Assert.Equal(3.8m, valuation.RateToBase);
        Assert.Equal(OperationDate, valuation.OperationDate);
    }

    [Fact]
    public void Later_rate_does_not_rewrite_an_earlier_valuation()
    {
        var original = new CurrencyAmount(100m, 1);
        var earlier = new BaseCurrencyValuation(original, 2, 3.8m, OperationDate);
        var later = new BaseCurrencyValuation(original, 2, 4m, OperationDate.AddDays(1));
        Assert.Equal(380m, earlier.ExactBaseAmount.Amount);
        Assert.Equal(3.8m, earlier.RateToBase);
        Assert.Equal(400m, later.ExactBaseAmount.Amount);
    }

    [Fact]
    public void Extra_rate_precision_is_preserved_until_explicit_rounding()
    {
        var valuation = new BaseCurrencyValuation(new CurrencyAmount(100m, 1), 2, 3.80000001m, OperationDate);
        Assert.Equal(380.000001m, valuation.ExactBaseAmount.Amount);
        Assert.Equal(new CurrencyAmount(380m, 2), valuation.ExactBaseAmount.Round(2));
        Assert.Equal(380.000001m, valuation.ExactBaseAmount.Amount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Nonpositive_rates_are_rejected(int rate) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new BaseCurrencyValuation(new CurrencyAmount(100m, 1), 2, rate, OperationDate));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Base_currency_reference_must_be_valid(int baseCurrencyId) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new BaseCurrencyValuation(new CurrencyAmount(100m, 1), baseCurrencyId, 1m, OperationDate));

    [Fact]
    public void Valuation_in_the_original_currency_requires_rate_one()
    {
        var original = new CurrencyAmount(100m, 1);
        Assert.Equal(original, new BaseCurrencyValuation(original, 1, 1m, OperationDate).ExactBaseAmount);
        Assert.Throws<ArgumentException>(() => new BaseCurrencyValuation(original, 1, 2m, OperationDate));
    }

    [Fact]
    public void Opposite_signed_values_at_the_same_rate_cancel_without_changing_the_snapshot()
    {
        var original = new CurrencyAmount(100m, 1);
        var positive = new BaseCurrencyValuation(original, 2, 3.8m, OperationDate);
        var negative = new BaseCurrencyValuation(original.Negate(), 2, positive.RateToBase, OperationDate);
        Assert.Equal(new CurrencyAmount(0m, 2), positive.ExactBaseAmount.Add(negative.ExactBaseAmount));
        Assert.Equal(380m, positive.ExactBaseAmount.Amount);
        Assert.Equal(0m, new BaseCurrencyValuation(new CurrencyAmount(0m, 1), 2, 3.8m, OperationDate).ExactBaseAmount.Amount);
    }

    [Fact]
    public void Conversion_overflow_is_rejected() =>
        Assert.Throws<OverflowException>(() => new BaseCurrencyValuation(new CurrencyAmount(decimal.MaxValue, 1), 2, 2m, OperationDate));

    [Fact]
    public void Missing_original_amount_is_rejected() =>
        Assert.Throws<ArgumentNullException>(() => new BaseCurrencyValuation(null!, 2, 1m, OperationDate));
}
