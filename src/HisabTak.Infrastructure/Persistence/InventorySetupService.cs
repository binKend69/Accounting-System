using HisabTak.Application.Inventory;
using HisabTak.Application.Security;
using HisabTak.Domain.Common;
using HisabTak.Domain.Security;
using HisabTak.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace HisabTak.Infrastructure.Persistence;

public sealed class InventorySetupService(HisabTakDbContext database, IAuthorizationService authorization) : IInventorySetupService
{
    private async Task Demand(int actorId, int branchId, CancellationToken ct)
    {
        await authorization.DemandAsync(actorId, PermissionCodes.SetupManage, ct);
        await AccountingAccess.BranchAsync(database, authorization, actorId, branchId, ct);
    }

    public async Task<InventorySetup> LoadAsync(int actorId, int branchId, CancellationToken ct = default)
    {
        await Demand(actorId, branchId, ct);
        var units = await database.Units.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.NameAr)
            .Select(x => new InventoryReference(x.Id, x.NameAr, null)).ToListAsync(ct);
        var warehouses = await database.Warehouses.AsNoTracking().Where(x => x.IsActive && x.BranchId == branchId)
            .OrderBy(x => x.NameAr).Select(x => new InventoryReference(x.Id, x.NameAr, x.BranchId)).ToListAsync(ct);
        var locations = await database.StockLocations.AsNoTracking().Where(x => x.IsActive &&
            database.Warehouses.Any(w => w.Id == x.WarehouseId && w.IsActive && w.BranchId == branchId))
            .OrderBy(x => x.NameAr).Select(x => new InventoryReference(x.Id, x.NameAr, x.WarehouseId)).ToListAsync(ct);
        return new(units, warehouses, locations);
    }

    public async Task<int> CreateAsync(int actorId, int branchId, InventoryReferenceKind kind, string name,
        int? warehouseId = null, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        name = name.Trim();
        if (name.Length > (kind == InventoryReferenceKind.Unit ? 50 : 100)) throw new ArgumentOutOfRangeException(nameof(name));
        if ((kind == InventoryReferenceKind.Location && warehouseId is not > 0) ||
            (kind != InventoryReferenceKind.Location && warehouseId is not null)) throw new ArgumentException("Invalid warehouse selection.");
        await using var transaction = await database.Database.BeginTransactionAsync(ct);
        await DatabaseLock.AcquireAsync(database, "HisabTak.Inventory.Setup", ct);
        await Demand(actorId, branchId, ct);
        if (kind == InventoryReferenceKind.Location && !await database.Warehouses.AnyAsync(
            x => x.Id == warehouseId && x.BranchId == branchId && x.IsActive, ct))
            throw new InvalidOperationException("Warehouse is unavailable in this branch.");
        // Include inactive/deleted rows: their identity remains reserved for future history.
        var duplicate = kind switch
        {
            InventoryReferenceKind.Unit => await database.Units.IgnoreQueryFilters().AnyAsync(x => x.NameAr == name, ct),
            InventoryReferenceKind.Warehouse => await database.Warehouses.IgnoreQueryFilters().AnyAsync(x => x.BranchId == branchId && x.NameAr == name, ct),
            _ => await database.StockLocations.IgnoreQueryFilters().AnyAsync(x => x.WarehouseId == warehouseId && x.NameAr == name, ct)
        };
        if (duplicate) throw new InvalidOperationException("This name is already reserved in its scope.");
        ReferenceEntity entity = kind switch
        {
            InventoryReferenceKind.Unit => new Unit(name),
            InventoryReferenceKind.Warehouse => new Warehouse(name, branchId),
            _ => new StockLocation(name, warehouseId)
        };
        database.Add(entity);
        await database.SaveChangesAsync(ct);
        database.AuditLogs.Add(new AuditLog(actorId, "INVENTORY_REFERENCE_CREATED", kind.ToString(), entity.Id, Environment.MachineName));
        await database.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return entity.Id;
    }
}
