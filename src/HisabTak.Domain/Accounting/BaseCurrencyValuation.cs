namespace HisabTak.Domain.Accounting;

/// <summary>
/// Historical valuation: one unit of the original currency equals RateToBase base units.
/// This immutable value does not select a current rate, round implicitly, or post a journal.
/// </summary>
public sealed record BaseCurrencyValuation
{
    public BaseCurrencyValuation(CurrencyAmount original, int baseCurrencyId, decimal rateToBase, DateOnly operationDate)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (baseCurrencyId <= 0) throw new ArgumentOutOfRangeException(nameof(baseCurrencyId));
        if (rateToBase <= 0) throw new ArgumentOutOfRangeException(nameof(rateToBase));
        if (original.CurrencyId == baseCurrencyId && rateToBase != 1m)
            throw new ArgumentException("A currency must have an identity rate against itself.", nameof(rateToBase));

        Original = original;
        RateToBase = rateToBase;
        OperationDate = operationDate;
        ExactBaseAmount = new CurrencyAmount(checked(original.Amount * rateToBase), baseCurrencyId);
    }

    public CurrencyAmount Original { get; }
    public CurrencyAmount ExactBaseAmount { get; }
    public decimal RateToBase { get; }
    public DateOnly OperationDate { get; }
}
