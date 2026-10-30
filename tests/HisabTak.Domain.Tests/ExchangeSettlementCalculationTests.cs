using HisabTak.Domain.Accounting;

namespace HisabTak.Domain.Tests;

public sealed class ExchangeSettlementCalculationTests
{
    private static BaseCurrencyValuation Debt(decimal principal = 100m) =>
        new(new CurrencyAmount(principal, 1), 2, 3.8m, new DateOnly(2026, 10, 30));

    [Theory]
    [InlineData(DebtDirection.Receivable, 400, 20, 0)]
    [InlineData(DebtDirection.Payable, 400, 0, 20)]
    [InlineData(DebtDirection.Receivable, 350, 0, 30)]
    [InlineData(DebtDirection.Payable, 350, 30, 0)]
    [InlineData(DebtDirection.Receivable, 380, 0, 0)]
    [InlineData(DebtDirection.Payable, 380, 0, 0)]
    public void Difference_has_the_correct_gain_or_loss_direction(DebtDirection direction, int payment, int gain, int loss)
    {
        var debt = Debt();
        var result = new ExchangeSettlementCalculation(debt, new CurrencyAmount(100m, 1), new CurrencyAmount(payment, 2), direction);
        Assert.Equal(new CurrencyAmount(gain, 2), result.Gain);
        Assert.Equal(new CurrencyAmount(loss, 2), result.Loss);
        Assert.Equal(new CurrencyAmount(380m, 2), result.HistoricalPrincipalInBase);
        Assert.Equal(new CurrencyAmount(0m, 1), result.RemainingPrincipal);
        Assert.Equal(380m, debt.ExactBaseAmount.Amount);
    }

    [Fact]
    public void Partial_settlement_recognizes_only_the_settled_principal()
    {
        var debt = Debt();
        var result = new ExchangeSettlementCalculation(debt, new CurrencyAmount(50m, 1), new CurrencyAmount(200m, 2), DebtDirection.Receivable);
        Assert.Equal(new CurrencyAmount(190m, 2), result.HistoricalPrincipalInBase);
        Assert.Equal(new CurrencyAmount(10m, 2), result.Gain);
        Assert.Equal(new CurrencyAmount(50m, 1), result.RemainingPrincipal);
        Assert.Equal(new CurrencyAmount(100m, 1), debt.Original);
        // Nominally different currencies cannot be combined even after valuation.
        Assert.Throws<InvalidOperationException>(() => result.RemainingPrincipal.Add(result.Gain));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public void Zero_negative_or_excess_principal_is_rejected(int principal) =>
        Assert.Throws<ArgumentException>(() => new ExchangeSettlementCalculation(Debt(), new CurrencyAmount(principal, 1), new CurrencyAmount(400m, 2), DebtDirection.Receivable));

    [Fact]
    public void Wrong_principal_or_payment_currency_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new ExchangeSettlementCalculation(Debt(), new CurrencyAmount(100m, 3), new CurrencyAmount(400m, 2), DebtDirection.Receivable));
        Assert.Throws<ArgumentException>(() => new ExchangeSettlementCalculation(Debt(), new CurrencyAmount(100m, 1), new CurrencyAmount(400m, 3), DebtDirection.Receivable));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Nonpositive_payment_is_rejected(int payment) =>
        Assert.Throws<ArgumentException>(() => new ExchangeSettlementCalculation(Debt(), new CurrencyAmount(100m, 1), new CurrencyAmount(payment, 2), DebtDirection.Payable));

    [Fact]
    public void Missing_inputs_and_unknown_direction_are_rejected()
    {
        var principal = new CurrencyAmount(100m, 1);
        var payment = new CurrencyAmount(400m, 2);
        Assert.Throws<ArgumentNullException>(() => new ExchangeSettlementCalculation(null!, principal, payment, DebtDirection.Receivable));
        Assert.Throws<ArgumentNullException>(() => new ExchangeSettlementCalculation(Debt(), null!, payment, DebtDirection.Receivable));
        Assert.Throws<ArgumentNullException>(() => new ExchangeSettlementCalculation(Debt(), principal, null!, DebtDirection.Receivable));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExchangeSettlementCalculation(Debt(), principal, payment, (DebtDirection)99));
        Assert.Throws<ArgumentException>(() => new ExchangeSettlementCalculation(Debt(0m), principal, payment, DebtDirection.Receivable));
    }

    [Fact]
    public void Payment_is_not_assumed_to_settle_the_whole_debt()
    {
        var result = new ExchangeSettlementCalculation(Debt(), new CurrencyAmount(25m, 1), new CurrencyAmount(100m, 2), DebtDirection.Payable);
        Assert.Equal(new CurrencyAmount(75m, 1), result.RemainingPrincipal);
        Assert.Equal(new CurrencyAmount(5m, 2), result.Loss);
        Assert.Equal(new CurrencyAmount(95m, 2), result.HistoricalPrincipalInBase);
    }

    [Fact]
    public void A_base_currency_debt_cannot_turn_an_overpayment_into_exchange_profit()
    {
        var debt = new BaseCurrencyValuation(new CurrencyAmount(100m, 2), 2, 1m, new DateOnly(2026, 10, 30));
        var result = new ExchangeSettlementCalculation(debt, debt.Original, new CurrencyAmount(100m, 2), DebtDirection.Receivable);
        Assert.Equal(0m, result.Gain.Amount);
        Assert.Equal(0m, result.Loss.Amount);
        Assert.Throws<ArgumentException>(() => new ExchangeSettlementCalculation(debt, debt.Original, new CurrencyAmount(120m, 2), DebtDirection.Receivable));
    }
}
