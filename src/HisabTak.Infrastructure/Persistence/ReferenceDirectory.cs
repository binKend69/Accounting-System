using HisabTak.Application.Security;
using HisabTak.Application.Inventory;
using HisabTak.Application.Setup;
using HisabTak.Domain.Common;
using HisabTak.Domain.Security;
using HisabTak.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace HisabTak.Infrastructure.Persistence;

internal sealed class ReferenceDirectory(HisabTakDbContext db, IAuthorizationService auth, IInventorySetupService inventory) : IReferenceDirectory
{
    private async Task Demand(int actor, int branch)
    {
        await auth.DemandAsync(actor, PermissionCodes.SetupManage);
        await AccountingAccess.BranchAsync(db, auth, actor, branch, default);
    }

    public async Task<IReadOnlyList<DirectoryRow>> ListAsync(int actor, int branch, DirectoryKind kind, int? warehouse = null)
    {
        await Demand(actor, branch);
        await ValidateWarehouse(branch, kind, warehouse);
        return kind switch
        {
            DirectoryKind.Center => await Rows(db.Centers.IgnoreQueryFilters().Where(x => x.BranchId == branch)),
            DirectoryKind.Department => await Rows(db.Departments.IgnoreQueryFilters()),
            DirectoryKind.Unit => await Rows(db.Units.IgnoreQueryFilters()),
            DirectoryKind.Warehouse => await Rows(db.Warehouses.IgnoreQueryFilters().Where(x => x.BranchId == branch)),
            DirectoryKind.Location => await Rows(db.StockLocations.IgnoreQueryFilters().Where(x => x.WarehouseId == warehouse)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private async Task ValidateWarehouse(int branch, DirectoryKind kind, int? warehouse)
    {
        if (kind == DirectoryKind.Location && !await db.Warehouses.AnyAsync(x => x.Id == warehouse && x.BranchId == branch && x.IsActive))
            throw new DirectoryConflictException("اختر مستودعًا نشطًا في الفرع.");
    }

    private static Task<List<DirectoryRow>> Rows<T>(IQueryable<T> records) where T : ReferenceEntity =>
        records.AsNoTracking().OrderBy(x => x.NameAr).ThenBy(x => x.Id)
            .Select(x => new DirectoryRow(x.Id, x.NameAr, x.IsDeleted, x.RowVersion)).ToListAsync();

    public async Task ApplyAsync(int actor, int branch, DirectoryKind kind, DirectoryAction action, DirectoryRow? original, string name, int? warehouse = null)
    {
        InventoryReferenceKind? inventoryKind = kind switch
        {
            DirectoryKind.Unit => InventoryReferenceKind.Unit,
            DirectoryKind.Warehouse => InventoryReferenceKind.Warehouse,
            DirectoryKind.Location => InventoryReferenceKind.Location,
            _ => null
        };
        if (action == DirectoryAction.Create && inventoryKind is { } target)
        {
            await inventory.CreateAsync(actor, branch, target, name, warehouse);
            return;
        }
        await using var tx = await db.Database.BeginTransactionAsync();
        await DatabaseLock.AcquireAsync(db, inventoryKind.HasValue ? "HisabTak.Inventory.Setup" : "HisabTak.ReferenceDirectory", default);
        await Demand(actor, branch);
        await ValidateWarehouse(branch, kind, warehouse);
        async Task Change<T>(IQueryable<T> records, Func<string,T> create) where T : ReferenceEntity
        {
            T entity;
            if (action is DirectoryAction.Create or DirectoryAction.Rename)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(name);
                name = name.Trim();
                if (name.Length > (kind == DirectoryKind.Unit ? 50 : 100)) throw new ArgumentOutOfRangeException(nameof(name));
                int except = action == DirectoryAction.Rename ? original?.Id ?? 0 : 0;
                if (await records.AnyAsync(x => x.NameAr == name && x.Id != except))
                    throw new DirectoryConflictException("الاسم محجوز لسجل ظاهر أو مخفي.");
            }
            if (action == DirectoryAction.Create)
            {
                entity = create(name);
                db.Add(entity);
            }
            else
            {
                if (original is null) throw new ArgumentException("اختر سجلًا.");
                entity = await records.SingleOrDefaultAsync(x => x.Id == original.Id)
                    ?? throw new DirectoryConflictException("السجل غير متاح.");
                if (!original.Version.SequenceEqual(entity.RowVersion))
                    throw new DirectoryConflictException("تغير السجل. حدّث القائمة قبل المحاولة مجددًا.");
                switch (action)
                {
                    case DirectoryAction.Rename when !entity.IsDeleted: entity.Rename(name); break;
                    case DirectoryAction.Hide when !entity.IsDeleted: entity.MarkDeleted(); break;
                    case DirectoryAction.Restore when entity.IsDeleted: entity.Restore(); break;
                    default: throw new DirectoryConflictException("العملية لا تتوافق مع حالة السجل.");
                }
            }
            await db.SaveChangesAsync();
            db.AuditLogs.Add(new AuditLog(actor, $"REFERENCE_{action.ToString().ToUpperInvariant()}", typeof(T).Name, entity.Id, Environment.MachineName));
        }

        switch (kind)
        {
            case DirectoryKind.Center: await Change(db.Centers.IgnoreQueryFilters().Where(x => x.BranchId == branch), n => new Center(n, branch)); break;
            case DirectoryKind.Department: await Change(db.Departments.IgnoreQueryFilters(), n => new Department(n)); break;
            case DirectoryKind.Unit: await Change(db.Units.IgnoreQueryFilters(), n => new Unit(n)); break;
            case DirectoryKind.Warehouse: await Change(db.Warehouses.IgnoreQueryFilters().Where(x => x.BranchId == branch), n => new Warehouse(n, branch)); break;
            case DirectoryKind.Location: await Change(db.StockLocations.IgnoreQueryFilters().Where(x => x.WarehouseId == warehouse), n => new StockLocation(n, warehouse)); break;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }
}
