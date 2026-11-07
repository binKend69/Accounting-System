using HisabTak.Domain.Accounting;

namespace HisabTak.Domain.Tests;

public sealed class LedgerRoundingTests
{
    [Fact]
    public void Separate_difference_balances_rounded_lines_without_changing_exact_values()
    {
        var date = new DateOnly(2026, 1, 1);
        var draft = JournalDraft.Create(1, new("year", date, date), date, Guid.NewGuid(), [
            new(1, JournalSide.Debit, new(new(0.01m, 2), 1, 1.5m, date)),
            new(2, JournalSide.Debit, new(new(0.01m, 2), 1, 1.5m, date)),
            new(3, JournalSide.Credit, new(new(0.03m, 1), 1, 1m, date))]);
        var result = LedgerRounding.Calculate(draft, 2, 4);
        Assert.Equal(4, result.Count);
        Assert.Equal(0.015m, result[0].Source.Valuation.ExactBaseAmount.Amount);
        Assert.Equal(0.02m, result[0].BookBaseAmount);
        Assert.True(result[3].IsRounding); Assert.Equal(JournalSide.Credit, result[3].Source.Side);
        Assert.Equal(0.01m, result[3].BookBaseAmount);
        Assert.Equal(0m, result.Sum(l => l.Source.Side == JournalSide.Debit ? l.BookBaseAmount : -l.BookBaseAmount));
        Assert.Throws<InvalidOperationException>(() => LedgerRounding.Calculate(draft, 2, 1));
    }

    [Fact]
    public void Last_settlement_consumes_remainder_and_does_not_leave_phantom_balance()
    {
        var principal = new CurrencyAmount(3m, 2); var book = new CurrencyAmount(100m, 1);
        var portions = new List<decimal>();
        for (var i = 0; i < 3; i++)
        {
            var amount = SettlementAllocation.Allocate(new(3m, 2), new(100m, 1), principal, new(1m, 2), book, 2);
            portions.Add(amount.Amount); book = book.Subtract(amount); principal = principal.Subtract(new(1m, 2));
        }
        Assert.Equal(new[] { 33.33m, 33.33m, 33.34m }, portions);
        Assert.Equal(0m, book.Amount); Assert.Equal(0m, principal.Amount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4)]
    public void Invalid_settlement_is_rejected(decimal amount) => Assert.Throws<ArgumentException>(() =>
        SettlementAllocation.Allocate(new(3m, 2), new(100m, 1), new(3m, 2), new(amount, 2), new(100m, 1), 2));

    [Fact]
    public void Final_settlement_uses_the_already_recorded_remainder() =>
        Assert.Equal(33.34m, SettlementAllocation.Allocate(new(3m, 2), new(100m, 1), new(1m, 2), new(1m, 2), new(33.34m, 1), 2).Amount);
}
