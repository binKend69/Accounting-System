using System.Globalization;

namespace MotoParts.Core.Common;

/// <summary>
/// An exact stock quantity with three decimal places.
///
/// Most parts are whole units, but oil is sold by the litre and cable and chain
/// by the metre, so quantities carry three decimals. Stored as an integer number
/// of thousandths for the same reason money is stored in minor units.
/// </summary>
public readonly struct Quantity : IEquatable<Quantity>, IComparable<Quantity>
{
    public const int Scale = 3;

    private readonly decimal _value;

    private Quantity(decimal roundedValue) => _value = roundedValue;

    public decimal Value => _value;

    /// <summary>The integer thousandths value persisted to the database.</summary>
    public long Thousandths => decimal.ToInt64(_value * 1000m);

    public static Quantity Zero => default;

    public static Quantity One => new(1m);

    public static Quantity Of(decimal value) =>
        new(Math.Round(value, Scale, MidpointRounding.AwayFromZero));

    public static Quantity FromThousandths(long thousandths) => new(thousandths / 1000m);

    public static bool TryParse(string? text, out Quantity quantity)
    {
        quantity = Zero;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var cleaned = text.Trim().Replace(",", string.Empty, StringComparison.Ordinal);

        if (!decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        quantity = Of(value);
        return true;
    }

    public bool IsZero => _value == 0m;

    public bool IsNegative => _value < 0m;

    public bool IsPositive => _value > 0m;

    public Quantity Abs() => new(Math.Abs(_value));

    public static Quantity operator +(Quantity a, Quantity b) => new(a._value + b._value);

    public static Quantity operator -(Quantity a, Quantity b) => new(a._value - b._value);

    public static Quantity operator -(Quantity a) => new(-a._value);

    public static bool operator ==(Quantity a, Quantity b) => a._value == b._value;

    public static bool operator !=(Quantity a, Quantity b) => a._value != b._value;

    public static bool operator <(Quantity a, Quantity b) => a._value < b._value;

    public static bool operator >(Quantity a, Quantity b) => a._value > b._value;

    public static bool operator <=(Quantity a, Quantity b) => a._value <= b._value;

    public static bool operator >=(Quantity a, Quantity b) => a._value >= b._value;

    public static Quantity Min(Quantity a, Quantity b) => a <= b ? a : b;

    public bool Equals(Quantity other) => _value == other._value;

    public override bool Equals(object? obj) => obj is Quantity other && Equals(other);

    public override int GetHashCode() => _value.GetHashCode();

    public int CompareTo(Quantity other) => _value.CompareTo(other._value);

    /// <summary>"2.5" rather than "2.500": trailing zeros dropped, never a locale separator.</summary>
    public override string ToString() =>
        _value.ToString("0.###", CultureInfo.InvariantCulture);

    public string ToDisplay() => _value.ToString("#,##0.###", CultureInfo.InvariantCulture);
}

public static class QuantityEnumerableExtensions
{
    public static Quantity Sum(this IEnumerable<Quantity> values)
    {
        var total = Quantity.Zero;

        foreach (var value in values)
        {
            total += value;
        }

        return total;
    }

    public static Quantity Sum<T>(this IEnumerable<T> source, Func<T, Quantity> selector) =>
        source.Select(selector).Sum();
}
