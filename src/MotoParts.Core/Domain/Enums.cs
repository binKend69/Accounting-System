namespace MotoParts.Core.Domain;

// Every enum here is stored in the database as its C# member name (TEXT), and
// each column carries a CHECK constraint listing the allowed names. A value
// written any other way - an integer, a typo - is rejected by the database
// rather than silently stored.

public enum AccountType
{
    Asset,
    Liability,
    Equity,
    Revenue,
    Expense,
}

public enum NormalBalance
{
    Debit,
    Credit,
}

public enum JournalSource
{
    Manual,
    Opening,
    Sale,
    SaleRefund,
    Purchase,
    StockAdjustment,
    CashDifference,
    CustomerReceipt,
    SupplierPayment,
    Expense,
    Payroll,
    PayrollPayment,
}

public enum PaymentMethod
{
    Cash,
    Card,
    Transfer,
    Credit,
}

public enum SaleStatus
{
    Completed,
    PartiallyRefunded,
    Refunded,
}

public enum PurchaseStatus
{
    Draft,
    Received,
    Cancelled,
}

public enum ShiftSessionStatus
{
    Open,
    Closed,
    ForceClosed,
}

public enum StockMovementType
{
    Opening,
    Purchase,
    Sale,
    SaleReturn,
    PurchaseReturn,
    Adjustment,
}

public enum PayrollStatus
{
    Draft,
    Posted,
    Paid,
}

public enum AuditAction
{
    Created,
    Updated,
    Deleted,
    Posted,
    Reversed,
    Locked,
    Unlocked,
    Opened,
    Closed,
    ForceClosed,
    Amended,
    Received,
    Cancelled,
    Paid,
    LoggedIn,
    LoginFailed,
    BackupCreated,
    BackupRestored,
}

/// <summary>Arabic labels and the business facts that hang off each enum value.</summary>
public static class EnumExtensions
{
    public static string ToArabic(this AccountType type) => type switch
    {
        AccountType.Asset => "أصول",
        AccountType.Liability => "خصوم",
        AccountType.Equity => "حقوق ملكية",
        AccountType.Revenue => "إيرادات",
        AccountType.Expense => "مصروفات",
        _ => type.ToString(),
    };

    /// <summary>Normal balance is fully determined by the account type.</summary>
    public static NormalBalance NormalBalance(this AccountType type) => type switch
    {
        AccountType.Asset or AccountType.Expense => Domain.NormalBalance.Debit,
        _ => Domain.NormalBalance.Credit,
    };

    public static bool IsBalanceSheet(this AccountType type) =>
        type is AccountType.Asset or AccountType.Liability or AccountType.Equity;

    public static string ToArabic(this NormalBalance balance) =>
        balance == Domain.NormalBalance.Debit ? "مدين" : "دائن";

    public static string ToArabic(this JournalSource source) => source switch
    {
        JournalSource.Manual => "قيد يدوي",
        JournalSource.Opening => "رصيد افتتاحي",
        JournalSource.Sale => "مبيعات",
        JournalSource.SaleRefund => "مرتجع مبيعات",
        JournalSource.Purchase => "مشتريات",
        JournalSource.StockAdjustment => "تسوية مخزون",
        JournalSource.CashDifference => "فرق صندوق",
        JournalSource.CustomerReceipt => "سند قبض",
        JournalSource.SupplierPayment => "سند صرف لمورد",
        JournalSource.Expense => "مصروف",
        JournalSource.Payroll => "مسير رواتب",
        JournalSource.PayrollPayment => "صرف رواتب",
        _ => source.ToString(),
    };

    public static string ToArabic(this PaymentMethod method) => method switch
    {
        PaymentMethod.Cash => "نقدي",
        PaymentMethod.Card => "بطاقة",
        PaymentMethod.Transfer => "تحويل بنكي",
        PaymentMethod.Credit => "آجل",
        _ => method.ToString(),
    };

    /// <summary>Only cash passes through the till drawer.</summary>
    public static bool AffectsCashDrawer(this PaymentMethod method) => method == PaymentMethod.Cash;

    /// <summary>Credit settles nothing now: it raises a receivable or a payable.</summary>
    public static bool IsDeferred(this PaymentMethod method) => method == PaymentMethod.Credit;

    public static string ToArabic(this SaleStatus status) => status switch
    {
        SaleStatus.Completed => "مكتملة",
        SaleStatus.PartiallyRefunded => "مرتجع جزئي",
        SaleStatus.Refunded => "مرتجعة بالكامل",
        _ => status.ToString(),
    };

    public static string ToArabic(this PurchaseStatus status) => status switch
    {
        PurchaseStatus.Draft => "مسودة",
        PurchaseStatus.Received => "مستلمة",
        PurchaseStatus.Cancelled => "ملغاة",
        _ => status.ToString(),
    };

    public static string ToArabic(this ShiftSessionStatus status) => status switch
    {
        ShiftSessionStatus.Open => "مفتوحة",
        ShiftSessionStatus.Closed => "مغلقة",
        ShiftSessionStatus.ForceClosed => "مغلقة إجبارياً",
        _ => status.ToString(),
    };

    public static string ToArabic(this StockMovementType type) => type switch
    {
        StockMovementType.Opening => "رصيد افتتاحي",
        StockMovementType.Purchase => "شراء",
        StockMovementType.Sale => "بيع",
        StockMovementType.SaleReturn => "مرتجع بيع",
        StockMovementType.PurchaseReturn => "مرتجع شراء",
        StockMovementType.Adjustment => "تسوية",
        _ => type.ToString(),
    };

    public static bool IsInbound(this StockMovementType type) =>
        type is StockMovementType.Opening or StockMovementType.Purchase or StockMovementType.SaleReturn;

    public static bool IsOutbound(this StockMovementType type) =>
        type is StockMovementType.Sale or StockMovementType.PurchaseReturn;

    public static string ToArabic(this PayrollStatus status) => status switch
    {
        PayrollStatus.Draft => "مسودة",
        PayrollStatus.Posted => "معتمد",
        PayrollStatus.Paid => "مصروف",
        _ => status.ToString(),
    };

    public static string ToArabic(this AuditAction action) => action switch
    {
        AuditAction.Created => "إنشاء",
        AuditAction.Updated => "تعديل",
        AuditAction.Deleted => "حذف",
        AuditAction.Posted => "ترحيل",
        AuditAction.Reversed => "عكس",
        AuditAction.Locked => "إقفال",
        AuditAction.Unlocked => "إعادة فتح",
        AuditAction.Opened => "فتح",
        AuditAction.Closed => "إغلاق",
        AuditAction.ForceClosed => "إغلاق إجباري",
        AuditAction.Amended => "تصحيح",
        AuditAction.Received => "استلام",
        AuditAction.Cancelled => "إلغاء",
        AuditAction.Paid => "صرف",
        AuditAction.LoggedIn => "تسجيل دخول",
        AuditAction.LoginFailed => "محاولة دخول فاشلة",
        AuditAction.BackupCreated => "نسخة احتياطية",
        AuditAction.BackupRestored => "استعادة نسخة",
        _ => action.ToString(),
    };
}
