using System.Text.Json;
using HisabTak.Application.Accounting;
using HisabTak.Application.Inventory;
using HisabTak.Domain.Security;

namespace HisabTak.Infrastructure.Persistence;

public sealed partial class StockDocumentService : IPosSlots
{
    private async Task<PosSlot> ReadSlot(int actor, int branch, int slot, CancellationToken ct)
    {
        if (slot is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(slot));
        await Branch(actor, branch, ct);
        var result = await Rows("SELECT Version, Payload, ReceiptId FROM dbo.PosSlots WHERE ActorId=@a AND BranchId=@b AND Slot=@s",
            r => new PosSlot(r.GetInt32(0), r.IsDBNull(1) ? null : JsonSerializer.Deserialize<StockDocumentDraft>(r.GetString(1)), r.IsDBNull(2) ? null : r.GetInt64(2)),
            ct, ("@a", actor), ("@b", branch), ("@s", slot));
        return result.SingleOrDefault() ?? new PosSlot(0, null);
    }

    public async Task<PosSlot> LoadAsync(int actor, int branch, int slot, CancellationToken ct = default)
    {
        await authorization.DemandAsync(actor, PermissionCodes.JournalPost, ct);
        return await ReadSlot(actor, branch, slot, ct);
    }

    public Task<PosSlot> SaveSlotAsync(int actor, int branch, int slot, int version, StockDocumentDraft? draft, CancellationToken ct = default) =>
        Write(actor, PermissionCodes.JournalPost, ct, async () =>
        {
            var old = await ReadSlot(actor, branch, slot, ct);
            if (version != old.Version) throw new DocumentConflictException();
            if (draft != null)
            {
                if (draft.BranchId != branch || draft.Kind != StockDocumentKind.Sale || draft.Credit)
                    throw new ArgumentException("Expected a cash sale in the selected branch.");
                await Validate(actor, draft, ct);
            }
            if (StockPayload.Encode(old.Draft) == StockPayload.Encode(draft)) return old;
            var payload = draft == null ? null : StockPayload.Encode(draft);
            var query = version == 0
                ? "INSERT dbo.PosSlots(ActorId,BranchId,Slot,Version,Payload) VALUES(@a,@b,@s,1,@p)"
                : "UPDATE dbo.PosSlots SET Version=Version+1,Payload=@p WHERE ActorId=@a AND BranchId=@b AND Slot=@s";
            await sql.ExecuteAsync(query, ct, ("@a", actor), ("@b", branch), ("@s", slot), ("@p", payload));
            return new PosSlot(version + 1, draft, old.LastReceiptId);
        });

    public Task<StockDocument> PayAsync(int actor, int branch, int slot, int version, CancellationToken ct = default) =>
        Write(actor, PermissionCodes.JournalPost, ct, async () =>
        {
            var current = await ReadSlot(actor, branch, slot, ct);
            var receipt = await sql.ScalarAsync("SELECT ReceiptId FROM dbo.PosSlots WHERE ActorId=@a AND BranchId=@b AND Slot=@s AND PaidVersion=@v",
                ct, ("@a", actor), ("@b", branch), ("@s", slot), ("@v", version));
            if (receipt != 0) return await Read(receipt, ct);
            if (current.Version != version || current.Draft == null) throw new DocumentConflictException();
            var kitchenItems = (await Rows("SELECT ProductId FROM dbo.StockProducts WHERE PrintInKitchen=1",
                reader => reader.GetInt32(0), ct)).ToHashSet();
            var lines = current.Draft.Lines.Select(line => line with { PrintInKitchen = kitchenItems.Contains(line.ProductId) }).ToArray();
            var sale = await SaveCore(actor, current.Draft with { SourceKey = Guid.NewGuid(), Lines = lines }, ct);
            var posted = await PostCore(actor, sale.Id, sale.Version, ct);
            await sql.ExecuteAsync("UPDATE dbo.PosSlots SET Version=Version+1, Payload=NULL, PaidVersion=@v, ReceiptId=@r WHERE ActorId=@a AND BranchId=@b AND Slot=@s",
                ct, ("@a", actor), ("@b", branch), ("@s", slot), ("@v", version), ("@r", posted.Id));
            return posted;
        });
}
