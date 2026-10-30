namespace HisabTak.Domain.Accounting;

/// <summary>
/// An exact decimal amount tagged with a currency from one merchant's database.
/// Rounding, exchange conversion and posting validity belong to explicit policies.
/// </summary>
public sealed record CurrencyAmount
{
    public CurrencyAmount(decimal amount, int currencyId)
    {
        if (currencyId <= 0) throw new ArgumentOutOfRangeException(nameof(currencyId));
        Amount = amount;
        CurrencyId = currencyId;
    }

    public decimal Amount { get; }
    public int CurrencyId { get; }

    public CurrencyAmount Add(CurrencyAmount other)
    {
        RequireSameCurrency(other);
        return new CurrencyAmount(checked(Amount + other.Amount), CurrencyId);
    }

    public CurrencyAmount Subtract(CurrencyAmount other)
    {
        RequireSameCurrency(other);
        return new CurrencyAmount(checked(Amount - other.Amount), CurrencyId);
    }

    public CurrencyAmount Negate() => new(-Amount, CurrencyId);

    private void RequireSameCurrency(CurrencyAmount other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (CurrencyId != other.CurrencyId)
            throw new InvalidOperationException("Amounts in different currencies require an explicit conversion.");
    }
}
