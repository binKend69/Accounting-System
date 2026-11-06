using HisabTak.Application.Inventory;
using HisabTak.Domain.Security;

namespace HisabTak.Infrastructure.Persistence;

public sealed partial class StockDocumentService
{
    public async Task<StockHistoryPage> HistoryAsync(int actor,int branchId,StockHistoryFilter filter,CancellationToken ct=default)
    {
        await authorization.DemandAsync(actor,PermissionCodes.JournalRead,ct);
        await Branch(actor,branchId,ct);
        if(filter.From>filter.To || filter.Page is <0 or >1000000 || filter.ProductId is <=0 || filter.WarehouseId is <=0 ||
            filter.Kind.HasValue && !Enum.IsDefined(filter.Kind.Value)) throw new ArgumentException("Invalid history filters.");
        // One row per saved invoice line, not per batch movement. Tax/net must not be repeated when a line spans batches.
        // Read posted and reversed documents: reversal is shown explicitly instead of erasing purchase/sale history.
        var rows=await Rows("""
            SELECT d.DocumentId,d.Number,d.EntryDate,d.Kind,d.State,d.ReversalDate,CONVERT(int,j.[key]),p.ProductId,
                p.Code+N' · '+p.Name,u.NameAr,w.NameAr,COALESCE(a.NameAr,N'—'),h.CurrencyId,c.Code,h.RateToBase,h.Credit,
                l.Quantity,l.UnitPrice,l.Discount,h.TaxEnabled,h.TaxRate,h.PricesIncludeTax,
                COALESCE(m.Net,0),COALESCE(m.Tax,0),COALESCE(m.Quantity,0),COALESCE(m.Value,0),l.OriginalMovementId,
                od.DocumentId,od.EntryDate,ol.UnitPrice,ol.Discount,ou.NameAr
            FROM dbo.StockDocuments d
            CROSS APPLY OPENJSON(d.Payload) WITH(CurrencyId int,RateToBase decimal(28,8),Credit bit,WarehouseId int,
                CounterpartyAccountId int,TaxEnabled bit,TaxRate decimal(28,8),PricesIncludeTax bit) h
            CROSS APPLY OPENJSON(d.Payload,'$.Lines') j
            CROSS APPLY OPENJSON(j.value) WITH(ProductId int,UnitId int,Quantity decimal(28,8),UnitPrice decimal(28,8),
                Discount decimal(28,8),OriginalMovementId bigint) l
            JOIN dbo.StockProducts p ON p.ProductId=l.ProductId
            JOIN dbo.Units u ON u.UnitId=l.UnitId
            JOIN dbo.Warehouses w ON w.WarehouseId=h.WarehouseId
            JOIN dbo.Currencies c ON c.CurrencyId=h.CurrencyId
            LEFT JOIN dbo.Accounts a ON a.AccountId=h.CounterpartyAccountId
            OUTER APPLY(SELECT SUM(sm.Net) Net,SUM(sm.Tax) Tax,SUM(sm.Quantity) Quantity,SUM(sm.Value) Value
                FROM dbo.StockMovements sm WHERE sm.DocumentId=d.DocumentId AND sm.LineNumber=CONVERT(int,j.[key])) m
            LEFT JOIN dbo.StockMovements om ON om.MovementId=l.OriginalMovementId
            LEFT JOIN dbo.StockDocuments od ON od.DocumentId=om.DocumentId AND od.BranchId=d.BranchId
            OUTER APPLY(SELECT q.UnitPrice,q.Discount,q.UnitId FROM OPENJSON(od.Payload,'$.Lines') oj
                CROSS APPLY OPENJSON(oj.value) WITH(UnitPrice decimal(28,8),Discount decimal(28,8),UnitId int) q
                WHERE CONVERT(int,oj.[key])=om.LineNumber) ol
            LEFT JOIN dbo.Units ou ON ou.UnitId=ol.UnitId
            WHERE d.Kind IN(2,3,4,5) AND d.BranchId=@b AND d.State IN(2,3) AND d.EntryDate BETWEEN @from AND @to
                AND (@p IS NULL OR l.ProductId=@p) AND (@w IS NULL OR h.WarehouseId=@w) AND (@kind IS NULL OR d.Kind=@kind)
            ORDER BY d.EntryDate DESC,d.DocumentId DESC,CONVERT(int,j.[key])
            OFFSET @offset ROWS FETCH NEXT 51 ROWS ONLY
            """,r=>new StockHistoryRow(r.GetInt64(0),r.GetInt64(1),DateOnly.FromDateTime(r.GetDateTime(2)),(StockDocumentKind)r.GetInt32(3),r.GetInt32(4),
                r.IsDBNull(5)?null:DateOnly.FromDateTime(r.GetDateTime(5)),r.GetInt32(6)+1,r.GetInt32(7),r.GetString(8),r.GetString(9),r.GetString(10),r.GetString(11),
                r.GetInt32(12),r.GetString(13),r.GetDecimal(14),r.GetBoolean(15),r.GetDecimal(16),r.GetDecimal(17),r.GetDecimal(18),r.GetBoolean(19),r.GetDecimal(20),r.GetBoolean(21),
                r.GetDecimal(22),r.GetDecimal(23),r.GetDecimal(24),r.GetDecimal(25),r.IsDBNull(26)?null:r.GetInt64(26),r.IsDBNull(27)?null:r.GetInt64(27),
                r.IsDBNull(28)?null:DateOnly.FromDateTime(r.GetDateTime(28)),r.IsDBNull(29)?null:r.GetDecimal(29),r.IsDBNull(30)?null:r.GetDecimal(30),r.IsDBNull(31)?null:r.GetString(31)),ct,
            ("@b",branchId),("@from",filter.From),("@to",filter.To),("@p",filter.ProductId),("@w",filter.WarehouseId),("@kind",filter.Kind is {} kind?(int)kind:null),("@offset",filter.Page*50));
        await Audit(actor,"STOCK_HISTORY_READ","Branches",branchId,ct);
        return new(rows.Take(50).ToArray(),rows.Count>50);
    }
}
