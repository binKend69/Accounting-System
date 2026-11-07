namespace HisabTak.Domain.Accounting;

public enum JournalSide { Debit = 1, Credit = 2 }
public enum JournalKind { Standard = 1, Opening = 2, Reversal = 3 }

public sealed record JournalLineDefinition
{
    public JournalLineDefinition(int accountId, JournalSide side, BaseCurrencyValuation valuation)
    {
        ArgumentNullException.ThrowIfNull(valuation);
        if (accountId <= 0) throw new ArgumentOutOfRangeException(nameof(accountId));
        if (side is not (JournalSide.Debit or JournalSide.Credit)) throw new ArgumentOutOfRangeException(nameof(side));
        if (valuation.Original.Amount <= 0 || valuation.ExactBaseAmount.Amount <= 0)
            throw new ArgumentException("A journal line must contain a positive amount on one side.", nameof(valuation));
        AccountId = accountId;
        Side = side;
        Valuation = valuation;
    }

    public int AccountId { get; }
    public JournalSide Side { get; }
    public BaseCurrencyValuation Valuation { get; }
    public JournalLineDefinition Reverse() => new(AccountId,
        Side == JournalSide.Debit ? JournalSide.Credit : JournalSide.Debit, Valuation);
}

/// <summary>
/// Validated immutable journal proposal. Account availability, source uniqueness, period locks,
/// posting precision and actual persistence must also be checked by the posting service.
/// </summary>
public sealed class JournalDraft
{
    private JournalDraft(JournalKind kind, int branchId, FiscalPeriod period, DateOnly date, Guid sourceKey,
        IEnumerable<JournalLineDefinition> lines, long? reversesEntryId = null)
    {
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(lines);
        if (branchId <= 0) throw new ArgumentOutOfRangeException(nameof(branchId));
        if (sourceKey == Guid.Empty) throw new ArgumentException("A source reference is required.", nameof(sourceKey));
        period.EnsurePostingAllowed(date);
        var copy = lines.ToArray();
        if (copy.Length < 2 || copy.Any(line => line is null))
            throw new ArgumentException("At least two valid journal lines are required.", nameof(lines));
        var baseId = copy[0].Valuation.ExactBaseAmount.CurrencyId;
        decimal debit = 0, credit = 0;
        foreach (var line in copy)
        {
            if (line.Valuation.ExactBaseAmount.CurrencyId != baseId)
                throw new InvalidOperationException("All lines must use the same base currency.");
            if (line.Side == JournalSide.Debit) debit = checked(debit + line.Valuation.ExactBaseAmount.Amount);
            else credit = checked(credit + line.Valuation.ExactBaseAmount.Amount);
        }
        if (debit != credit) throw new InvalidOperationException("Journal is not balanced in base currency.");
        Kind = kind;
        BranchId = branchId;
        EntryDate = date;
        SourceKey = sourceKey;
        ReversesEntryId = reversesEntryId;
        Lines = Array.AsReadOnly(copy);
    }

    public JournalKind Kind { get; }
    public int BranchId { get; }
    public DateOnly EntryDate { get; }
    public Guid SourceKey { get; }
    public long? ReversesEntryId { get; }
    public IReadOnlyList<JournalLineDefinition> Lines { get; }

    public static JournalDraft Create(int branchId, FiscalPeriod period, DateOnly date, Guid sourceKey,
        IEnumerable<JournalLineDefinition> lines) => new(JournalKind.Standard, branchId, period, date, sourceKey, lines);

    public static JournalDraft CreateOpening(int branchId, FiscalPeriod period, DateOnly date, Guid sourceKey,
        IEnumerable<JournalLineDefinition> lines) => new(JournalKind.Opening, branchId, period, date, sourceKey, lines);

    public JournalDraft Reverse(long originalEntryId, FiscalPeriod targetPeriod, DateOnly reversalDate, Guid sourceKey)
    {
        if (originalEntryId <= 0) throw new ArgumentOutOfRangeException(nameof(originalEntryId));
        if (sourceKey == SourceKey) throw new ArgumentException("Reversal needs its own source reference.", nameof(sourceKey));
        return new JournalDraft(JournalKind.Reversal, BranchId, targetPeriod, reversalDate, sourceKey,
            Lines.Select(line => line.Reverse()), originalEntryId);
    }
}
