using System.Globalization;

namespace MotoParts.Core.Common;

/// <summary>
/// An exact monetary amount with two decimal places.
///
/// Stored in the database as an integer number of minor units (halalas/cents):
/// SQLite has no exact decimal type and would otherwise keep 0.10 as a binary
/// float. In memory it is a <see cref="decimal"/>, which is exact base-10.
///
/// Every operation rounds once, half away from zero, so 0.1 + 0.2 style drift
/// cannot accumulate and every figure on screen is the figure in the ledger.
/// Keeping money a distinct type means the compiler rejects adding a price to
/// a quantity, or passing a tax rate where an amount was expected.
/// </summary>
public readonly struct Money : IEquatable<Money>, IComparable<Money>
{
    public const int Scale = 2;

    private readonly decimal _amount;

    private Money(decimal roundedAmount) => _amount = roundedAmount;

    public decimal Amount => _amount;

    /// <summary>The integer minor-unit value persisted to the database.</summary>
    public long MinorUnits => decimal.ToInt64(_amount * 100m);

    public static Money Zero => default;

    public static Money Of(decimal amount) =>
        new(Math.Round(amount, Scale, MidpointRounding.AwayFromZero));

    public static Money FromMinorUnits(long minorUnits) => new(minorUnits / 100m);

    /// <summary>Parses user input such as "1,234.50". Returns false for anything else.</summary>
    public static bool TryParse(string? text, out Money money)
    {
        money = Zero;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var cleaned = text.Trim().Replace(",", string.Empty, StringComparison.Ordinal);

        if (!decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        money = Of(value);
        return true;
    }

    public bool IsZero => _amount == 0m;

    public bool IsNegative => _amount < 0m;

    public bool IsPositive => _amount > 0m;

    public Money Abs() => new(Math.Abs(_amount));

    public static Money operator +(Money a, Money b) => new(a._amount + b._amount);

    public static Money operator -(Money a, Money b) => new(a._amount - b._amount);

    public static Money operator -(Money a) => new(-a._amount);

    /// <summary>
    /// Multiply by a rate or factor that is not itself money (a tax rate such
    /// as 0.15). The factor keeps its full precision until the single rounding.
    /// </summary>
    public static Money operator *(Money a, decimal factor) => Of(a._amount * factor);

    public static Money operator *(Money a, Quantity quantity) => Of(a._amount * quantity.Value);

    public static bool operator ==(Money a, Money b) => a._amount == b._amount;

    public static bool operator !=(Money a, Money b) => a._amount != b._amount;

    public static bool operator <(Money a, Money b) => a._amount < b._amount;

    public static bool operator >(Money a, Money b) => a._amount > b._amount;

    public static bool operator <=(Money a, Money b) => a._amount <= b._amount;

    public static bool operator >=(Money a, Money b) => a._amount >= b._amount;

    public static Money Max(Money a, Money b) => a >= b ? a : b;

    public static Money Min(Money a, Money b) => a <= b ? a : b;

    public bool Equals(Money other) => _amount == other._amount;

    public override bool Equals(object? obj) => obj is Money other && Equals(other);

    public override int GetHashCode() => _amount.GetHashCode();

    public int CompareTo(Money other) => _amount.CompareTo(other._amount);

    /// <summary>Always "1234.50" style, independent of the machine's locale.</summary>
    public override string ToString() => _amount.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>"1,234.50" - the display form used on screens and documents.</summary>
    public string ToDisplay() => _amount.ToString("#,##0.00", CultureInfo.InvariantCulture);
}

public static class MoneyEnumerableExtensions
{
    public static Money Sum(this IEnumerable<Money> values)
    {
        var total = Money.Zero;

        foreach (var value in values)
        {
            total += value;
        }

        return total;
    }

    public static Money Sum<T>(this IEnumerable<T> source, Func<T, Money> selector) =>
        source.Select(selector).Sum();
}
