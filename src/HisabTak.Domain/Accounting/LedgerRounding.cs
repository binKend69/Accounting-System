namespace HisabTak.Domain.Accounting;

public sealed record RoundedLedgerLine(JournalLineDefinition Source, decimal BookBaseAmount, bool IsRounding);

public static class LedgerRounding
{
    public static IReadOnlyList<RoundedLedgerLine> Calculate(JournalDraft draft, int precision, int roundingAccountId)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (precision is < 0 or > 8) throw new ArgumentOutOfRangeException(nameof(precision));
        if (roundingAccountId <= 0) throw new ArgumentOutOfRangeException(nameof(roundingAccountId));
        if (draft.Lines.Any(x => x.AccountId == roundingAccountId))
            throw new InvalidOperationException("The rounding account is reserved for generated differences.");
        var lines = draft.Lines.Select(l => new RoundedLedgerLine(l,
            decimal.Round(l.Valuation.ExactBaseAmount.Amount, precision, MidpointRounding.AwayFromZero), false)).ToList();
        var difference = lines.Sum(l => l.Source.Side == JournalSide.Debit ? l.BookBaseAmount : -l.BookBaseAmount);
        if (difference != 0)
        {
            var amount = decimal.Abs(difference);
            var baseId = draft.Lines[0].Valuation.ExactBaseAmount.CurrencyId;
            lines.Add(new(new(roundingAccountId, difference > 0 ? JournalSide.Credit : JournalSide.Debit,
                new(new(amount, baseId), baseId, 1m, draft.EntryDate)), amount, true));
        }
        return lines.AsReadOnly();
    }
}

/// <summary>Allocate the remaining historical book value; the last payment consumes its exact remainder.</summary>
public static class SettlementAllocation
{
    public static CurrencyAmount Allocate(CurrencyAmount originalPrincipal, CurrencyAmount originalBookValue,
        CurrencyAmount remainingPrincipal, CurrencyAmount settledPrincipal,
        CurrencyAmount remainingBookValue, int precision)
    {
        ArgumentNullException.ThrowIfNull(originalPrincipal);
        ArgumentNullException.ThrowIfNull(originalBookValue);
        ArgumentNullException.ThrowIfNull(remainingPrincipal);
        ArgumentNullException.ThrowIfNull(settledPrincipal);
        ArgumentNullException.ThrowIfNull(remainingBookValue);
        if (precision is < 0 or > 8) throw new ArgumentOutOfRangeException(nameof(precision));
        if (originalPrincipal.Amount <= 0 || originalBookValue.Amount < 0
            || remainingPrincipal.CurrencyId != originalPrincipal.CurrencyId || remainingPrincipal.Amount > originalPrincipal.Amount
            || remainingBookValue.CurrencyId != originalBookValue.CurrencyId || remainingBookValue.Amount > originalBookValue.Amount
            || remainingPrincipal.Amount <= 0 || remainingBookValue.Amount < 0 || settledPrincipal.Amount <= 0
            || settledPrincipal.Amount > remainingPrincipal.Amount || settledPrincipal.CurrencyId != remainingPrincipal.CurrencyId)
            throw new ArgumentException("Settlement must be within the remaining principal and book value.");
        if (decimal.Round(remainingBookValue.Amount, precision) != remainingBookValue.Amount)
            throw new ArgumentException("Remaining book value must already use the posting precision.");
        var allocated = settledPrincipal.Amount == remainingPrincipal.Amount ? remainingBookValue.Amount
            : decimal.Round(checked(originalBookValue.Amount * (settledPrincipal.Amount / originalPrincipal.Amount)),
                precision, MidpointRounding.AwayFromZero);
        return new(Math.Min(allocated, remainingBookValue.Amount), remainingBookValue.CurrencyId);
    }
}
