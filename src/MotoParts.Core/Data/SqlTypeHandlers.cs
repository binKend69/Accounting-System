using System.Data;
using System.Globalization;
using Dapper;
using MotoParts.Core.Common;

namespace MotoParts.Core.Data;

/// <summary>
/// Teaches Dapper how this schema stores its values: money as integer minor
/// units, quantities as integer thousandths, dates and times as ISO text.
///
/// Enums are deliberately not handled here - Dapper ignores custom handlers for
/// enum types. They are written with <c>.ToString()</c> (guarded by the CHECK
/// lists in the schema) and read back through Dapper's own name parsing.
/// </summary>
public static class SqlTypeHandlers
{
    public const string DateFormat = "yyyy-MM-dd";
    public const string DateTimeFormat = "yyyy-MM-dd HH:mm:ss";
    public const string TimeFormat = "HH:mm";

    private static int _registered;

    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1)
        {
            return;
        }

        // snake_case columns map onto PascalCase properties.
        DefaultTypeMap.MatchNamesWithUnderscores = true;

        SqlMapper.AddTypeHandler(new MoneyHandler());
        SqlMapper.AddTypeHandler(new QuantityHandler());
        SqlMapper.AddTypeHandler(new DateOnlyHandler());
        SqlMapper.AddTypeHandler(new TimeOnlyHandler());
        SqlMapper.AddTypeHandler(new DateTimeHandler());
    }

    public static string ToDb(this DateOnly date) => date.ToString(DateFormat, CultureInfo.InvariantCulture);

    public static string ToDb(this DateTime dateTime) => dateTime.ToString(DateTimeFormat, CultureInfo.InvariantCulture);

    public static string ToDb(this TimeOnly time) => time.ToString(TimeFormat, CultureInfo.InvariantCulture);

    public static DateOnly ParseDate(string text) =>
        DateOnly.ParseExact(text, DateFormat, CultureInfo.InvariantCulture);

    public static DateTime ParseDateTime(string text) =>
        DateTime.ParseExact(
            text.Length > DateTimeFormat.Length ? text[..DateTimeFormat.Length] : text,
            text.Length == DateFormat.Length ? DateFormat : DateTimeFormat,
            CultureInfo.InvariantCulture);

    private static long ToInt64(object value) => value switch
    {
        long l => l,
        int i => i,
        double d => checked((long)Math.Round(d, MidpointRounding.AwayFromZero)),
        decimal m => decimal.ToInt64(m),
        string s => long.Parse(s, CultureInfo.InvariantCulture),
        _ => Convert.ToInt64(value, CultureInfo.InvariantCulture),
    };

    private sealed class MoneyHandler : SqlMapper.TypeHandler<Money>
    {
        public override void SetValue(IDbDataParameter parameter, Money value)
        {
            parameter.DbType = DbType.Int64;
            parameter.Value = value.MinorUnits;
        }

        public override Money Parse(object value) => Money.FromMinorUnits(ToInt64(value));
    }

    private sealed class QuantityHandler : SqlMapper.TypeHandler<Quantity>
    {
        public override void SetValue(IDbDataParameter parameter, Quantity value)
        {
            parameter.DbType = DbType.Int64;
            parameter.Value = value.Thousandths;
        }

        public override Quantity Parse(object value) => Quantity.FromThousandths(ToInt64(value));
    }

    private sealed class DateOnlyHandler : SqlMapper.TypeHandler<DateOnly>
    {
        public override void SetValue(IDbDataParameter parameter, DateOnly value)
        {
            parameter.DbType = DbType.String;
            parameter.Value = value.ToDb();
        }

        public override DateOnly Parse(object value) => value switch
        {
            DateOnly d => d,
            DateTime dt => DateOnly.FromDateTime(dt),
            _ => ParseDate(((string)value)[..DateFormat.Length]),
        };
    }

    private sealed class TimeOnlyHandler : SqlMapper.TypeHandler<TimeOnly>
    {
        public override void SetValue(IDbDataParameter parameter, TimeOnly value)
        {
            parameter.DbType = DbType.String;
            parameter.Value = value.ToDb();
        }

        public override TimeOnly Parse(object value) => value switch
        {
            TimeOnly t => t,
            _ => TimeOnly.Parse((string)value, CultureInfo.InvariantCulture),
        };
    }

    private sealed class DateTimeHandler : SqlMapper.TypeHandler<DateTime>
    {
        public override void SetValue(IDbDataParameter parameter, DateTime value)
        {
            parameter.DbType = DbType.String;
            parameter.Value = value.ToDb();
        }

        public override DateTime Parse(object value) => value switch
        {
            DateTime dt => dt,
            _ => ParseDateTime((string)value),
        };
    }
}
