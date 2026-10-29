namespace MotoParts.Core.Common;

/// <summary>
/// A request the business rules refuse, with a message fit to show the operator.
///
/// Every message is written for the person at the counter, in Arabic, and says
/// what to do next where there is something to do. Anything that is not a
/// <see cref="BusinessRuleException"/> is a bug, and the UI treats it as one.
/// </summary>
public class BusinessRuleException : Exception
{
    public BusinessRuleException(string message)
        : base(message)
    {
    }

    public BusinessRuleException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>A sale or issue would take a product below zero.</summary>
public sealed class InsufficientStockException : BusinessRuleException
{
    public InsufficientStockException(string sku, Quantity requested, Quantity available)
        : base($"الكمية غير متوفرة للصنف {sku}: المطلوب {requested}، والمتوفر {available} فقط.")
    {
        Sku = sku;
        Requested = requested;
        Available = available;
    }

    public string Sku { get; }

    public Quantity Requested { get; }

    public Quantity Available { get; }
}

/// <summary>The operator tried to sell or refund without an open till.</summary>
public sealed class NoOpenShiftException : BusinessRuleException
{
    public NoOpenShiftException()
        : base("لا توجد وردية مفتوحة باسمك. افتح وردية أولاً قبل البيع أو الاسترجاع.")
    {
    }
}

/// <summary>A posting date falls in a period that is missing or closed.</summary>
public sealed class ClosedFiscalPeriodException : BusinessRuleException
{
    private ClosedFiscalPeriodException(string message)
        : base(message)
    {
    }

    public static ClosedFiscalPeriodException Missing(DateOnly date) =>
        new($"لا توجد فترة محاسبية تغطي تاريخ {date:yyyy-MM-dd}. أنشئ الفترة من شاشة الفترات المحاسبية.");

    public static ClosedFiscalPeriodException Locked(DateOnly date) =>
        new($"الفترة المحاسبية التي تشمل تاريخ {date:yyyy-MM-dd} مقفلة، ولا يمكن تسجيل أي قيد فيها.");
}

/// <summary>The signed-in user lacks the permission an action needs.</summary>
public sealed class AccessDeniedException : BusinessRuleException
{
    public AccessDeniedException(string message = "ليست لديك صلاحية لتنفيذ هذا الإجراء.")
        : base(message)
    {
    }
}
