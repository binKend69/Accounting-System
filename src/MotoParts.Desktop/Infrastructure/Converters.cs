using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using MotoParts.Core.Common;
using MotoParts.Core.Domain;

namespace MotoParts.Desktop.Infrastructure;

/// <summary>Money as "1,234.50"; null as an em dash.</summary>
public sealed class MoneyConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        Money m => m.ToDisplay(),
        decimal d => Money.Of(d).ToDisplay(),
        _ => "—",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Quantity as "2.5"; null as an em dash.</summary>
public sealed class QuantityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        Quantity q => q.ToDisplay(),
        _ => "—",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Dates as yyyy-MM-dd, date-times as yyyy-MM-dd HH:mm (or the format in the parameter).</summary>
public sealed class DateConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        DateTime dt => dt.ToString(parameter as string ?? "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        _ => "—",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Any domain enum to its Arabic label.</summary>
public sealed class ArabicEnumConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        AccountType v => v.ToArabic(),
        NormalBalance v => v.ToArabic(),
        JournalSource v => v.ToArabic(),
        PaymentMethod v => v.ToArabic(),
        SaleStatus v => v.ToArabic(),
        PurchaseStatus v => v.ToArabic(),
        ShiftSessionStatus v => v.ToArabic(),
        StockMovementType v => v.ToArabic(),
        PayrollStatus v => v.ToArabic(),
        AuditAction v => v.ToArabic(),
        null => "—",
        _ => value.ToString() ?? "",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Red for negative money, green for positive when the parameter is "signed", otherwise default.</summary>
public sealed class MoneySignBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var money = value as Money?;

        if (money is { IsNegative: true })
        {
            return Application.Current.FindResource("Danger");
        }

        if (money is { IsPositive: true } && parameter as string == "signed")
        {
            return Application.Current.FindResource("Warning");
        }

        return Application.Current.FindResource("Text");
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;

        if (parameter as string == "invert")
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var present = value is not null && !(value is string s && string.IsNullOrWhiteSpace(s));

        if (parameter as string == "invert")
        {
            present = !present;
        }

        return present ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>"نعم" / "لا" for flags shown in grids.</summary>
public sealed class YesNoConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "نعم" : "لا";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Badge colours by status name: the brush resource to use for a status value.</summary>
public sealed class StatusBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            SaleStatus.Completed or PurchaseStatus.Received or PayrollStatus.Paid or ShiftSessionStatus.Closed => "SuccessSoft",
            SaleStatus.Refunded or PurchaseStatus.Cancelled or ShiftSessionStatus.ForceClosed => "DangerSoft",
            SaleStatus.PartiallyRefunded or PurchaseStatus.Draft or PayrollStatus.Draft => "WarningSoft",
            ShiftSessionStatus.Open or PayrollStatus.Posted => "PrimarySoft",
            true => "SuccessSoft",
            false => "DangerSoft",
            _ => "PrimarySoft",
        };

        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Logical not, for IsReadOnly = !IsEditable and paired radio buttons.</summary>
public sealed class InvertBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>Visible only when every bound flag is true.</summary>
public sealed class AllTrueVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.All(v => v is true) ? Visibility.Visible : Visibility.Collapsed;

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>The audit trail's record types (stored as class names) in Arabic.</summary>
public sealed class AuditTypeConverter : IValueConverter
{
    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        ["Account"] = "حساب",
        ["Attendance"] = "حضور",
        ["Backup"] = "نسخة احتياطية",
        ["Category"] = "فئة",
        ["Customer"] = "عميل",
        ["CustomerReceipt"] = "سند قبض",
        ["ExpenseVoucher"] = "سند مصروف",
        ["FiscalPeriod"] = "فترة محاسبية",
        ["JournalEntry"] = "قيد يومية",
        ["PayrollRun"] = "مسير رواتب",
        ["Product"] = "صنف",
        ["Purchase"] = "فاتورة شراء",
        ["Sale"] = "فاتورة بيع",
        ["SaleRefund"] = "مرتجع",
        ["Setting"] = "إعداد",
        ["ShiftSession"] = "وردية",
        ["Supplier"] = "مورد",
        ["SupplierPayment"] = "سند صرف",
        ["User"] = "مستخدم",
    };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string type && Labels.TryGetValue(type, out var label) ? label : value ?? "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
