using HisabTak.Domain.Accounting;
using HisabTak.Domain.Security;
using HisabTak.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace HisabTak.Infrastructure.Persistence;

public sealed class ReferenceDataSeeder(HisabTakDbContext database)
{
    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await DatabaseLock.AcquireAsync(database, "HisabTak.ReferenceSeed", cancellationToken);

        var currencyCodes = await database.Currencies.IgnoreQueryFilters().Select(x => x.Code).ToListAsync(cancellationToken);
        foreach (var (code, name, symbol) in new[] { ("YER", "ريال يمني", "ر.ي"), ("SAR", "ريال سعودي", "ر.س"), ("USD", "دولار أمريكي", "$") })
            if (!currencyCodes.Contains(code, StringComparer.OrdinalIgnoreCase)) database.Currencies.Add(new Currency(code, name, symbol));

        var units = await database.Units.IgnoreQueryFilters().Select(x => x.NameAr).ToListAsync(cancellationToken);
        foreach (var name in new[] { "حبة", "كرتون", "كيلو", "لتر", "متر" })
            if (!units.Contains(name, StringComparer.Ordinal)) database.Units.Add(new Unit(name));

        var generalCodes = await database.AccountGeneralGroups.IgnoreQueryFilters().Select(x => x.SystemCode).ToListAsync(cancellationToken);
        foreach (var (code, name) in new[] { ("CASH", "صناديق"), ("BANK", "بنوك"), ("CUSTOMER", "عملاء"), ("SUPPLIER", "موردون") })
            if (!generalCodes.Contains(code, StringComparer.OrdinalIgnoreCase)) database.AccountGeneralGroups.Add(new AccountGeneralGroup(code, name));

        var groupCodes = await database.AccountMainGroups.IgnoreQueryFilters().Select(x => x.Code).ToListAsync(cancellationToken);
        foreach (var (code, name) in new[] { ("1", "الأصول"), ("2", "الالتزامات وحقوق الملكية"), ("3", "المصروفات"), ("4", "الإيرادات") })
            if (!groupCodes.Contains(code, StringComparer.Ordinal)) database.AccountMainGroups.Add(new AccountMainGroup(code, name));

        var types = await database.DocumentTypes.IgnoreQueryFilters().Select(x => x.Code).ToListAsync(cancellationToken);
        foreach (var (code, name, category) in DocumentDefinitions)
            if (!types.Contains(code, StringComparer.OrdinalIgnoreCase)) database.DocumentTypes.Add(new DocumentType(code, name, category));

        var roles = await database.Roles.IgnoreQueryFilters().Select(x => x.Code).ToListAsync(cancellationToken);
        foreach (var (code, name) in new[] { ("OWNER", "المالك"), ("CASHIER", "كاشير"), ("ACCOUNTANT", "محاسب"), ("BRANCH_MANAGER", "مدير الفرع") })
            if (!roles.Contains(code, StringComparer.OrdinalIgnoreCase)) database.Roles.Add(new Role(code, name, systemRole: true));

        // Only positive, exact grants. No implicit parent-to-child privileges.
        foreach (var (rootCode, rootName, children) in PermissionDefinitions)
        {
            var parent = await database.Permissions.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Code == rootCode, cancellationToken);
            if (parent is null)
            {
                parent = new Permission(rootCode, rootName, rootCode);
                database.Permissions.Add(parent);
                await database.SaveChangesAsync(cancellationToken);
            }
            foreach (var (code, name) in children)
                if (!await database.Permissions.IgnoreQueryFilters().AnyAsync(x => x.Code == code, cancellationToken))
                    database.Permissions.Add(new Permission(code, name, rootCode, parent.Id));
        }
        await database.SaveChangesAsync(cancellationToken);
        var owner = await database.Roles.SingleAsync(x => x.Code == "OWNER", cancellationToken);
        var permissionIds = await database.Permissions.Where(x => x.ParentPermissionId != null).Select(x => x.Id).ToListAsync(cancellationToken);
        var granted = await database.RolePermissions.Where(x => x.RoleId == owner.Id).Select(x => x.PermissionId).ToListAsync(cancellationToken);
        foreach (var permissionId in permissionIds.Except(granted)) database.RolePermissions.Add(new RolePermission(owner.Id, permissionId));

        // Seed no customer company, branch, account, balance, user, password, tax rate or FX rate.
        database.AuditLogs.Add(new AuditLog(null, "REFERENCE_SEEDED", "Setup", null, Environment.MachineName));
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static readonly (string Code, string Name, byte Category)[] DocumentDefinitions =
    [
        ("SALES_CASH", "بيع نقدي", 1), ("SALES_CREDIT", "بيع آجل", 1),
        ("PURCHASE_CASH", "شراء نقدي", 1), ("PURCHASE_CREDIT", "شراء آجل", 1),
        ("SALES_RETURN_CASH", "مردود بيع نقدي", 1), ("SALES_RETURN_CREDIT", "مردود بيع آجل", 1),
        ("PURCHASE_RETURN_CASH", "مردود شراء نقدي", 1), ("PURCHASE_RETURN_CREDIT", "مردود شراء آجل", 1),
        ("QUOTATION", "عرض سعر", 1), ("PURCHASE_ORDER", "أمر شراء", 1),
        ("CUSTOMS_CLEARANCE", "تخليص جمركي", 1), ("STOCK_TRANSFER", "تحويل مخزني", 3),
        ("STOCK_ISSUE", "صرف مخزني", 3), ("OPENING_BALANCE", "افتتاحي مخزون", 3),
        ("STOCK_ADJUST_INCREASE", "زيادة جرد", 3), ("STOCK_ADJUST_DECREASE", "نقص جرد", 3),
        ("RECEIPT_VOUCHER", "سند قبض", 2), ("PAYMENT_VOUCHER", "سند صرف", 2),
        ("JOURNAL_VOUCHER", "قيد يومية", 2), ("JOURNAL_OPENING", "قيد افتتاحي", 2)
    ];

    private static readonly (string Code, string Name, (string Code, string Name)[] Children)[] PermissionDefinitions =
    [
        ("SETUP", "الإعداد", [(PermissionCodes.SetupManage, "إدارة الإعداد")]),
        ("SECURITY", "المستخدمون", [(PermissionCodes.UsersManage, "إدارة المستخدمين")]),
        ("AUDIT", "التدقيق", [(PermissionCodes.AuditRead, "عرض التدقيق")]),
        ("ACCOUNTING", "المحاسبة", [(PermissionCodes.PeriodClose, "إقفال الفترة المالية"), (PermissionCodes.PeriodReopen, "إعادة فتح الفترة المالية")]),
        ("BACKUP", "النسخ والاستعادة", [(PermissionCodes.BackupCreate, "إنشاء نسخة"), (PermissionCodes.BackupRestore, "استعادة إلى قاعدة جديدة")])
    ];
}
