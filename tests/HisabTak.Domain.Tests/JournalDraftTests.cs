using HisabTak.Domain.Accounting;

namespace HisabTak.Domain.Tests;

public sealed class JournalDraftTests
{
    private static readonly DateOnly Date = new(2026, 1, 1);
    private static FiscalPeriod Period() => new("2026", Date, Date.AddMonths(11).AddDays(30));
    private static JournalLineDefinition Line(int account, JournalSide side, decimal amount, int currency = 1, decimal rate = 1m, int baseId = 1) =>
        new(account, side, new BaseCurrencyValuation(new CurrencyAmount(amount, currency), baseId, rate, Date));
    private static JournalLineDefinition[] Balanced() => [Line(1, JournalSide.Debit, 100m), Line(2, JournalSide.Credit, 100m)];

    [Fact]
    public void Opening_is_a_distinct_balanced_journal_with_a_source_reference()
    {
        var key = Guid.NewGuid();
        var draft = JournalDraft.CreateOpening(1, Period(), Date, key, Balanced());
        Assert.Equal(JournalKind.Opening, draft.Kind);
        Assert.Equal(key, draft.SourceKey);
        Assert.Null(draft.ReversesEntryId);
    }

    [Fact]
    public void Empty_one_line_and_unbalanced_journals_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => JournalDraft.Create(1, Period(), Date, Guid.NewGuid(), []));
        Assert.Throws<ArgumentException>(() => JournalDraft.Create(1, Period(), Date, Guid.NewGuid(), [Balanced()[0]]));
        Assert.Throws<InvalidOperationException>(() => JournalDraft.Create(1, Period(), Date, Guid.NewGuid(),
            [Line(1, JournalSide.Debit, 100m), Line(2, JournalSide.Credit, 90m)]));
    }

    [Fact]
    public void Equal_nominal_amounts_in_different_currencies_are_not_a_balance()
    {
        Assert.Throws<InvalidOperationException>(() => JournalDraft.Create(1, Period(), Date, Guid.NewGuid(),
            [Line(1, JournalSide.Debit, 100m, currency: 2, rate: 3.8m), Line(2, JournalSide.Credit, 100m)]));
    }

    [Fact]
    public void Explicit_foreign_exchange_values_can_balance_without_losing_original_currency()
    {
        var draft = JournalDraft.Create(1, Period(), Date, Guid.NewGuid(),
            [Line(1, JournalSide.Debit, 400m), Line(2, JournalSide.Credit, 100m, currency: 2, rate: 3.8m), Line(3, JournalSide.Credit, 20m)]);
        Assert.Equal(100m, draft.Lines[1].Valuation.Original.Amount);
        Assert.Equal(2, draft.Lines[1].Valuation.Original.CurrencyId);
        Assert.Equal(380m, draft.Lines[1].Valuation.ExactBaseAmount.Amount);
    }

    [Fact]
    public void Lines_with_different_base_currencies_are_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => JournalDraft.Create(1, Period(), Date, Guid.NewGuid(),
            [Line(1, JournalSide.Debit, 100m), Line(2, JournalSide.Credit, 100m, currency: 2, baseId: 2)]));
    }

    [Fact]
    public void Closed_period_cannot_receive_an_opening_or_standard_journal()
    {
        var period = Period();
        period.Close(1, DateTime.UtcNow);
        Assert.Throws<InvalidOperationException>(() => JournalDraft.CreateOpening(1, period, Date, Guid.NewGuid(), Balanced()));
        Assert.Throws<InvalidOperationException>(() => JournalDraft.Create(1, period, Date, Guid.NewGuid(), Balanced()));
    }

    [Fact]
    public void Reversal_uses_an_open_target_period_and_preserves_the_original()
    {
        var oldPeriod = Period();
        var original = JournalDraft.Create(2, oldPeriod, Date, Guid.NewGuid(), Balanced());
        oldPeriod.Close(1, DateTime.UtcNow);
        var nextYear = new FiscalPeriod("2027", Date.AddYears(1), Date.AddYears(2).AddDays(-1));
        var reversal = original.Reverse(123, nextYear, Date.AddYears(1), Guid.NewGuid());
        Assert.Equal(JournalKind.Reversal, reversal.Kind);
        Assert.Equal(123L, reversal.ReversesEntryId);
        Assert.Equal(2, reversal.BranchId);
        Assert.Equal(JournalSide.Credit, reversal.Lines[0].Side);
        Assert.Equal(JournalSide.Debit, original.Lines[0].Side);
        Assert.Equal(original.Lines[0].Valuation, reversal.Lines[0].Valuation);
        Assert.Equal(Date, original.EntryDate);
        Assert.Equal(Date.AddYears(1), reversal.EntryDate);
        Assert.Throws<InvalidOperationException>(() => original.Reverse(123, oldPeriod, Date, Guid.NewGuid()));
    }

    [Fact]
    public void Journal_lines_cannot_be_changed_via_the_input_collection_or_public_list()
    {
        var lines = Balanced().ToList();
        var draft = JournalDraft.Create(1, Period(), Date, Guid.NewGuid(), lines);
        lines.Clear();
        Assert.Equal(2, draft.Lines.Count);
        Assert.Throws<NotSupportedException>(() => ((IList<JournalLineDefinition>)draft.Lines).Clear());
    }

    [Fact]
    public void Invalid_line_and_source_references_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Line(0, JournalSide.Debit, 1m));
        Assert.Throws<ArgumentOutOfRangeException>(() => Line(1, (JournalSide)99, 1m));
        Assert.Throws<ArgumentException>(() => Line(1, JournalSide.Debit, 0m));
        Assert.Throws<ArgumentException>(() => Line(1, JournalSide.Debit, -1m));
        Assert.Throws<ArgumentException>(() => JournalDraft.Create(1, Period(), Date, Guid.Empty, Balanced()));
        var original = JournalDraft.Create(1, Period(), Date, Guid.NewGuid(), Balanced());
        Assert.Throws<ArgumentOutOfRangeException>(() => original.Reverse(0, Period(), Date, Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => original.Reverse(1, Period(), Date, original.SourceKey));
    }
}
