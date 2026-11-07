using HisabTak.Domain.Common;

namespace HisabTak.Domain.Setup;

public sealed class Currency : ReferenceEntity
{
    private Currency() { }
    public Currency(string code, string name, string symbol) : base(name)
    {
        Code = RequiredText(code, 3).ToUpperInvariant();
        if (Code.Length != 3 || Code.Any(c => c is < 'A' or > 'Z')) throw new ArgumentException("Invalid currency code.", nameof(code));
        Symbol = RequiredText(symbol, 10);
    }

    public string Code { get; private set; } = string.Empty;
    public string Symbol { get; private set; } = string.Empty;
    public string? FractionName { get; private set; }
    // Null explicitly means unconfigured. No fabricated exchange-rate parity.
    public decimal? ExchangeRate { get; private set; }
    public byte ConversionType { get; private set; } = 1;
    public bool IsBaseCurrency { get; private set; }
}
