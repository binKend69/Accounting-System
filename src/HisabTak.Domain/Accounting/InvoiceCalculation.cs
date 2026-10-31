namespace HisabTak.Domain.Accounting;

public sealed record InvoiceLineInput(decimal Quantity, decimal UnitPrice, decimal DiscountAmount = 0m);
public sealed record CalculatedInvoiceLine(decimal Net, decimal Tax, decimal Total);

/// <summary>Immutable calculation snapshot. DiscountAmount is a line amount in the invoice currency.</summary>
public sealed class InvoiceCalculation
{
    private InvoiceCalculation(int currencyId, int decimals, bool taxEnabled, decimal taxRate,
        bool pricesIncludeTax, IReadOnlyList<CalculatedInvoiceLine> lines)
    {
        CurrencyId = currencyId;
        DecimalPlaces = decimals;
        TaxEnabled = taxEnabled;
        TaxRate = taxRate;
        PricesIncludeTax = pricesIncludeTax;
        Lines = lines;
        Net = lines.Sum(x => x.Net);
        Tax = lines.Sum(x => x.Tax);
        Total = checked(Net + Tax);
    }

    public int CurrencyId { get; }
    public int DecimalPlaces { get; }
    public bool TaxEnabled { get; }
    public decimal TaxRate { get; }
    public bool PricesIncludeTax { get; }
    public IReadOnlyList<CalculatedInvoiceLine> Lines { get; }
    public decimal Net { get; }
    public decimal Tax { get; }
    public decimal Total { get; }

    public static InvoiceCalculation Calculate(int currencyId, int decimalPlaces,
        IEnumerable<InvoiceLineInput> lines, bool taxEnabled = false,
        decimal? taxRate = null, bool pricesIncludeTax = false)
    {
        if (currencyId <= 0) throw new ArgumentOutOfRangeException(nameof(currencyId));
        if (decimalPlaces is < 0 or > 28) throw new ArgumentOutOfRangeException(nameof(decimalPlaces));
        ArgumentNullException.ThrowIfNull(lines);
        if (taxEnabled && (taxRate is null || taxRate < 0 || taxRate > 1))
            throw new ArgumentOutOfRangeException(nameof(taxRate), "An explicit tax fraction between zero and one is required.");
        // Disabled tax never inherits an old rate or an inclusive-price flag.
        var rate = taxEnabled ? taxRate!.Value : 0m;
        var inclusive = taxEnabled && pricesIncludeTax;
        decimal Round(decimal value) => decimal.Round(value, decimalPlaces, MidpointRounding.AwayFromZero);
        var result = new List<CalculatedInvoiceLine>();
        foreach (var line in lines)
        {
            ArgumentNullException.ThrowIfNull(line);
            if (line.Quantity <= 0 || line.UnitPrice < 0 || line.DiscountAmount < 0)
                throw new ArgumentException("Quantity must be positive; price and line discount cannot be negative.", nameof(lines));
            var gross = checked(line.Quantity * line.UnitPrice);
            if (line.DiscountAmount > gross) throw new ArgumentException("Discount exceeds the line amount.", nameof(lines));
            var discounted = Round(gross - line.DiscountAmount);
            var net = inclusive ? Round(discounted / (1m + rate)) : discounted;
            var tax = inclusive ? discounted - net : Round(checked(net * rate));
            result.Add(new CalculatedInvoiceLine(net, tax, checked(net + tax)));
        }
        if (result.Count == 0) throw new ArgumentException("At least one line is required.", nameof(lines));
        return new InvoiceCalculation(currencyId, decimalPlaces, taxEnabled, rate, inclusive, result.AsReadOnly());
    }
}
