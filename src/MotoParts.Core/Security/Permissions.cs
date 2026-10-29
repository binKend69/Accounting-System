namespace MotoParts.Core.Security;

/// <summary>
/// Every permission the system checks, as constants so a typo is a compile
/// error rather than a silently open door.
/// </summary>
public static class Permission
{
    // Accounting
    public const string AccountingView = "accounting.view";
    public const string AccountingManageAccounts = "accounting.manage_accounts";
    public const string AccountingPostManualEntry = "accounting.post_manual_entry";
    public const string AccountingReverseEntry = "accounting.reverse_entry";
    public const string FiscalPeriodManage = "fiscal_period.manage";
    public const string FiscalPeriodLock = "fiscal_period.lock";
    public const string FiscalPeriodUnlock = "fiscal_period.unlock";

    // Inventory
    public const string InventoryView = "inventory.view";
    public const string InventoryManageProducts = "inventory.manage_products";
    public const string InventoryAdjustStock = "inventory.adjust_stock";

    // Purchasing and suppliers
    public const string PurchaseView = "purchase.view";
    public const string PurchaseManage = "purchase.manage";
    public const string PurchaseReceive = "purchase.receive";
    public const string SupplierPay = "supplier.pay";

    // Sales and customers
    public const string SaleCreate = "sale.create";
    public const string SaleViewOwn = "sale.view_own";
    public const string SaleViewAll = "sale.view_all";
    public const string SaleRefund = "sale.refund";
    public const string CustomerManage = "customer.manage";
    public const string CustomerReceive = "customer.receive";

    // Expenses
    public const string ExpenseCreate = "expense.create";
    public const string VoucherVoid = "voucher.void";

    // Shifts and attendance
    public const string ShiftManageOwn = "shift.manage_own";
    public const string ShiftViewAll = "shift.view_all";
    public const string ShiftForceClose = "shift.force_close";
    public const string ShiftManageTemplates = "shift.manage_templates";
    public const string AttendanceRecordOwn = "attendance.record_own";
    public const string AttendanceViewAll = "attendance.view_all";

    // Payroll
    public const string PayrollManage = "payroll.manage";

    // Reports
    public const string ReportViewOwn = "report.view_own";
    public const string ReportViewAll = "report.view_all";

    // Administration
    public const string UserManage = "user.manage";
    public const string AuditView = "audit.view";
    public const string SettingsManage = "settings.manage";
    public const string BackupManage = "backup.manage";

    public static IReadOnlyList<string> All { get; } =
        typeof(Permission).GetFields()
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();
}

public static class RoleName
{
    public const string Owner = "owner";
    public const string Accountant = "accountant";
    public const string Salesperson = "salesperson";
}

/// <summary>The default grant for each role, applied by the seeder.</summary>
public static class RoleDefaults
{
    public static IReadOnlyDictionary<string, (string DisplayName, IReadOnlyList<string> Permissions)> Roles { get; } =
        new Dictionary<string, (string, IReadOnlyList<string>)>
        {
            // Everything, including closing periods and overriding tills.
            [RoleName.Owner] = ("المالك", Permission.All),

            // Runs a till, reconciles it and keeps the books; does not close
            // periods, override other people's tills or administer users.
            [RoleName.Accountant] = ("المحاسب",
            [
                Permission.AccountingView,
                Permission.InventoryView,
                Permission.PurchaseView,
                Permission.SaleCreate,
                Permission.SaleViewOwn,
                Permission.SaleRefund,
                Permission.CustomerManage,
                Permission.CustomerReceive,
                Permission.SupplierPay,
                Permission.ExpenseCreate,
                Permission.ShiftManageOwn,
                Permission.AttendanceRecordOwn,
                Permission.ReportViewOwn,
            ]),

            // The till. Shift management covers only their own sessions: a sale
            // is only valid inside the seller's own open session, so without it
            // a salesperson could never sell.
            [RoleName.Salesperson] = ("البائع",
            [
                Permission.InventoryView,
                Permission.SaleCreate,
                Permission.SaleViewOwn,
                Permission.ShiftManageOwn,
                Permission.AttendanceRecordOwn,
            ]),
        };
}
