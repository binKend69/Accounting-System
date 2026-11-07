namespace HisabTak.Domain.Accounting;

public enum DebtDirection
{
    Receivable = 1,
    Payable = 2
}

/// <summary>
/// Exact-value settlement calculation for an unrevalued debt, without fees.
/// The caller explicitly supplies the principal being settled and actual payment in base units.
/// Persistence, rounded carrying-value allocation and duplicate-payment protection are separate.
/// </summary>
public sealed record ExchangeSettlementCalculation
{
    public ExchangeSettlementCalculation(BaseCurrencyValuation debt, CurrencyAmount settledPrincipal,
        CurrencyAmount actualPaymentInBase, DebtDirection direction)
    {
        ArgumentNullException.ThrowIfNull(debt);
        ArgumentNullException.ThrowIfNull(settledPrincipal);
        ArgumentNullException.ThrowIfNull(actualPaymentInBase);
        if (direction is not (DebtDirection.Receivable or DebtDirection.Payable))
            throw new ArgumentOutOfRangeException(nameof(direction));
        if (debt.Original.Amount <= 0)
            throw new ArgumentException("A positive outstanding debt is required.", nameof(debt));
        if (settledPrincipal.CurrencyId != debt.Original.CurrencyId
            || settledPrincipal.Amount <= 0 || settledPrincipal.Amount > debt.Original.Amount)
            throw new ArgumentException("Principal must be positive, in the debt currency, and within the outstanding amount.", nameof(settledPrincipal));
        if (actualPaymentInBase.CurrencyId != debt.ExactBaseAmount.CurrencyId || actualPaymentInBase.Amount <= 0)
            throw new ArgumentException("Payment must be positive and valued in the same base currency.", nameof(actualPaymentInBase));
        if (debt.Original.CurrencyId == debt.ExactBaseAmount.CurrencyId
            && actualPaymentInBase.Amount != settledPrincipal.Amount)
            throw new ArgumentException("A difference on base-currency principal is not automatically an exchange gain or loss.", nameof(actualPaymentInBase));

        SettledPrincipal = settledPrincipal;
        ActualPaymentInBase = actualPaymentInBase;
        HistoricalPrincipalInBase = new CurrencyAmount(checked(settledPrincipal.Amount * debt.RateToBase),
            debt.ExactBaseAmount.CurrencyId);
        var difference = checked(actualPaymentInBase.Amount - HistoricalPrincipalInBase.Amount);
        var signedGain = direction == DebtDirection.Receivable ? difference : -difference;
        Gain = new CurrencyAmount(Math.Max(signedGain, 0m), debt.ExactBaseAmount.CurrencyId);
        Loss = new CurrencyAmount(Math.Max(-signedGain, 0m), debt.ExactBaseAmount.CurrencyId);
        RemainingPrincipal = debt.Original.Subtract(settledPrincipal);
    }

    public CurrencyAmount SettledPrincipal { get; }
    public CurrencyAmount HistoricalPrincipalInBase { get; }
    public CurrencyAmount ActualPaymentInBase { get; }
    public CurrencyAmount Gain { get; }
    public CurrencyAmount Loss { get; }
    public CurrencyAmount RemainingPrincipal { get; }
}
