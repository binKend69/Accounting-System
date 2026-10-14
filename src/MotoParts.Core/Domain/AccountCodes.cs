namespace MotoParts.Core.Domain;

/// <summary>
/// Codes of the accounts the services post to by name. Seeded as system
/// accounts, which the schema refuses to delete or retype.
///
/// The usual blocks: 1xxx assets, 2xxx liabilities, 3xxx equity, 4xxx revenue,
/// 5xxx-6xxx expenses, so an accountant can read the ledger without a legend.
/// </summary>
public static class AccountCodes
{
    public const string Cash = "1000";
    public const string Bank = "1010";
    public const string AccountsReceivable = "1100";
    public const string Inventory = "1200";

    public const string AccountsPayable = "2000";
    public const string TaxPayable = "2100";
    public const string SalariesPayable = "2200";

    public const string OwnersEquity = "3000";
    public const string RetainedEarnings = "3100";
    public const string OpeningBalanceEquity = "3900";

    public const string SalesRevenue = "4000";
    public const string SalesReturns = "4100";

    public const string CostOfGoodsSold = "5000";
    public const string SalariesExpense = "6000";
    public const string CashOverShort = "6100";
    public const string InventoryAdjustment = "6200";
}

public sealed record ChartAccount(string Code, string Name, AccountType Type, string Description, bool IsSystem);

/// <summary>The default chart of accounts.</summary>
public static class DefaultChartOfAccounts
{
    public static IReadOnlyList<ChartAccount> Accounts { get; } =
    [
        // Assets
        new(AccountCodes.Cash, "النقدية في الصندوق", AccountType.Asset,
            "النقد الفعلي في درج الوردية والخزنة. يُطابَق مع كل وردية.", true),
        new(AccountCodes.Bank, "البنك", AccountType.Asset,
            "متحصلات البطاقات والتحويلات البنكية.", true),
        new(AccountCodes.AccountsReceivable, "الذمم المدينة (العملاء)", AccountType.Asset,
            "المستحق على العملاء من المبيعات الآجلة.", true),
        new(AccountCodes.Inventory, "المخزون", AccountType.Asset,
            "قطع الغيار المعدة للبيع، بالتكلفة.", true),

        // Liabilities
        new(AccountCodes.AccountsPayable, "الذمم الدائنة (الموردون)", AccountType.Liability,
            "المستحق للموردين عن المشتريات الآجلة.", true),
        new(AccountCodes.TaxPayable, "ضريبة القيمة المضافة", AccountType.Liability,
            "ضريبة المخرجات المحصلة ناقص ضريبة المدخلات القابلة للخصم.", true),
        new(AccountCodes.SalariesPayable, "الرواتب المستحقة", AccountType.Liability,
            "رواتب معتمدة لم تُصرف بعد.", true),

        // Equity
        new(AccountCodes.OwnersEquity, "رأس المال", AccountType.Equity,
            "ما ضخه المالك في المنشأة.", true),
        new(AccountCodes.RetainedEarnings, "الأرباح المحتجزة", AccountType.Equity,
            "الأرباح المتراكمة المرحلة من سنوات سابقة.", true),
        new(AccountCodes.OpeningBalanceEquity, "أرصدة افتتاحية", AccountType.Equity,
            "الطرف المقابل للأرصدة الافتتاحية عند بدء استخدام النظام.", true),

        // Revenue
        new(AccountCodes.SalesRevenue, "إيرادات المبيعات", AccountType.Revenue,
            "مبيعات قطع الغيار بدون الضريبة.", true),
        new(AccountCodes.SalesReturns, "مرتجعات ومسموحات المبيعات", AccountType.Revenue,
            "حساب مقابل للإيرادات يُخفضها بقيمة المرتجعات.", true),

        // Expenses
        new(AccountCodes.CostOfGoodsSold, "تكلفة البضاعة المباعة", AccountType.Expense,
            "تكلفة القطع لحظة بيعها.", true),
        new(AccountCodes.SalariesExpense, "مصروف الرواتب", AccountType.Expense,
            "رواتب الموظفين.", true),
        new(AccountCodes.CashOverShort, "عجز وزيادة الصندوق", AccountType.Expense,
            "فروقات عدّ الصندوق عند إغلاق الورديات.", true),
        new(AccountCodes.InventoryAdjustment, "تسويات المخزون", AccountType.Expense,
            "التالف والفاقد وفروقات الجرد.", true),

        // Everyday expenses for expense vouchers. Not system accounts: the shop
        // can rename, extend or retire them.
        new("6300", "الإيجار", AccountType.Expense, "إيجار المحل والمستودع.", false),
        new("6400", "الكهرباء والماء والاتصالات", AccountType.Expense, "فواتير الخدمات.", false),
        new("6500", "النقل والتوصيل", AccountType.Expense, "أجور النقل والشحن.", false),
        new("6600", "الصيانة", AccountType.Expense, "صيانة المحل والمعدات.", false),
        new("6700", "رسوم بنكية", AccountType.Expense, "عمولات أجهزة الدفع والتحويلات.", false),
        new("6900", "مصروفات عمومية وإدارية", AccountType.Expense, "مصروفات متنوعة.", false),
    ];
}
