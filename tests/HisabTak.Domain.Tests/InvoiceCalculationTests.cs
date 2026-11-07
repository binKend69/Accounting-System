using HisabTak.Domain.Accounting;

namespace HisabTak.Domain.Tests;

public sealed class InvoiceCalculationTests
{
    [Fact]
    public void Discount_precedes_exclusive_tax()
    {
        var result = InvoiceCalculation.Calculate(1, 2, [new(1, 100, 10)], true, .1m);
        Assert.Equal(90m, result.Net);
        Assert.Equal(9m, result.Tax);
        Assert.Equal(99m, result.Total);
    }

    [Fact]
    public void Inclusive_price_is_preserved_after_discount()
    {
        var result = InvoiceCalculation.Calculate(1, 2, [new(1, 110, 11)], true, .1m, true);
        Assert.Equal(90m, result.Net);
        Assert.Equal(9m, result.Tax);
        Assert.Equal(99m, result.Total);
    }

    [Fact]
    public void Totals_sum_rounded_lines_instead_of_recalculating_tax()
    {
        var result = InvoiceCalculation.Calculate(1, 2, [new(1, .05m), new(1, .05m)], true, .1m);
        Assert.Equal(.10m, result.Net);
        Assert.Equal(.02m, result.Tax);
        Assert.Equal(.12m, result.Total);
    }

    [Fact]
    public void Tax_is_disabled_by_default_and_stale_settings_are_ignored()
    {
        var result = InvoiceCalculation.Calculate(1, 2, [new(1, 110)]);
        Assert.False(result.TaxEnabled);
        Assert.Equal(0m, result.Tax);
        Assert.Equal(110m, result.Total);
        var stale = InvoiceCalculation.Calculate(1, 2, [new(1, 110)], false, .1m, true);
        Assert.Equal(result.Total, stale.Total);
        Assert.Equal(0m, stale.TaxRate);
        Assert.False(stale.PricesIncludeTax);
    }

    [Fact]
    public void Inclusive_rounding_never_changes_the_agreed_total()
    {
        var result = InvoiceCalculation.Calculate(1, 2, [new(1, .05m)], true, .1m, true);
        Assert.Equal(.05m, result.Total);
        Assert.Equal(result.Total, result.Net + result.Tax);
    }

    [Fact]
    public void Invalid_input_and_missing_tax_rate_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => InvoiceCalculation.Calculate(0, 2, [new(1, 10)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => InvoiceCalculation.Calculate(1, 29, [new(1, 10)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => InvoiceCalculation.Calculate(1, 2, [new(1, 10)], true));
        Assert.Throws<ArgumentOutOfRangeException>(() => InvoiceCalculation.Calculate(1, 2, [new(1, 10)], true, -.1m));
        Assert.Throws<ArgumentOutOfRangeException>(() => InvoiceCalculation.Calculate(1, 2, [new(1, 10)], true, 1.1m));
        Assert.Throws<ArgumentException>(() => InvoiceCalculation.Calculate(1, 2, []));
        Assert.Throws<ArgumentException>(() => InvoiceCalculation.Calculate(1, 2, [new(0, 10)]));
        Assert.Throws<ArgumentException>(() => InvoiceCalculation.Calculate(1, 2, [new(1, -10)]));
        Assert.Throws<ArgumentException>(() => InvoiceCalculation.Calculate(1, 2, [new(1, 10, -1)]));
        Assert.Throws<ArgumentException>(() => InvoiceCalculation.Calculate(1, 2, [new(1, 10, 11)]));
    }

    [Fact]
    public void Snapshot_is_immutable_and_overflow_fails_explicitly()
    {
        var inputs = new List<InvoiceLineInput> { new(1, 1.005m) };
        var result = InvoiceCalculation.Calculate(1, 2, inputs);
        inputs.Clear();
        Assert.Equal(1.01m, result.Total);
        Assert.Throws<NotSupportedException>(() => ((IList<CalculatedInvoiceLine>)result.Lines).Clear());
        Assert.Throws<OverflowException>(() => InvoiceCalculation.Calculate(1, 2, [new(2, decimal.MaxValue)]));
    }

    [Fact]
    public void Currency_precision_and_full_discount_are_explicit()
    {
        Assert.Equal(1m, InvoiceCalculation.Calculate(1, 0, [new(1, 1.4m)]).Total);
        Assert.Equal(1.235m, InvoiceCalculation.Calculate(1, 3, [new(1, 1.2345m)]).Total);
        Assert.Equal(0m, InvoiceCalculation.Calculate(1, 2, [new(1, 10, 10)], true, .1m).Total);
    }
}
