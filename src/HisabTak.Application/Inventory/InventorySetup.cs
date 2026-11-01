namespace HisabTak.Application.Inventory;

public enum InventoryReferenceKind { Unit = 1, Warehouse = 2, Location = 3 }
public sealed record InventoryReference(int Id, string Name, int? ParentId);
public sealed record InventorySetup(IReadOnlyList<InventoryReference> Units,
    IReadOnlyList<InventoryReference> Warehouses, IReadOnlyList<InventoryReference> Locations);

public interface IInventorySetupService
{
    Task<InventorySetup> LoadAsync(int actorId, int branchId, CancellationToken ct = default);
    Task<int> CreateAsync(int actorId, int branchId, InventoryReferenceKind kind, string name,
        int? warehouseId = null, CancellationToken ct = default);
}
