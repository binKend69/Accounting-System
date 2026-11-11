namespace HisabTak.Application.Inventory;

public sealed record PosSlot(int Version, StockDocumentDraft? Draft, long? LastReceiptId = null);

public interface IPosSlots
{
    Task<PosSlot> LoadAsync(int actor, int branch, int slot, CancellationToken ct = default);
    Task<PosSlot> SaveSlotAsync(int actor, int branch, int slot, int version, StockDocumentDraft? draft, CancellationToken ct = default);
    Task<StockDocument> PayAsync(int actor, int branch, int slot, int version, CancellationToken ct = default);
}
