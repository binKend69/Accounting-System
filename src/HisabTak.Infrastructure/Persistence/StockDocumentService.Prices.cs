using HisabTak.Application.Inventory;
using HisabTak.Domain.Inventory;
using HisabTak.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace HisabTak.Infrastructure.Persistence;

public sealed partial class StockDocumentService
{
    public Task SetBarcodeAsync(int actor,int productId,int unitId,string barcode,CancellationToken ct=default) =>
        Write(actor,PermissionCodes.SetupManage,ct,async () =>
        {
            await sql.ExecuteAsync("INSERT dbo.StockUnitBarcodes(Barcode,ProductId,UnitId) VALUES(@c,@p,@u)",ct,("@c",Name(barcode,80)),("@p",productId),("@u",unitId));
            await Audit(actor,"UNIT_BARCODE_SET","StockProducts",productId,ct);return productId;
        });
    public async Task<ProductUnitInfo?> ScanAsync(int actor,string barcode,CancellationToken ct=default)
    {
        await authorization.DemandAsync(actor,PermissionCodes.JournalRead,ct);
        return (await Rows("""
            SELECT u.ProductId,u.UnitId,u.Factor FROM dbo.StockUnitBarcodes b JOIN dbo.StockProductUnits u ON u.ProductId=b.ProductId AND u.UnitId=b.UnitId
            JOIN dbo.Units a ON a.UnitId=u.UnitId AND a.IsDeleted=0 AND a.IsActive=1 WHERE b.Barcode=@c
            """,r=>new ProductUnitInfo(r.GetInt32(0),r.GetInt32(1),r.GetDecimal(2)),ct,("@c",Name(barcode,80)))).SingleOrDefault();
    }
    public Task SetPriceAsync(int actor,int productId,int unitId,int currencyId,string tier,decimal price,CancellationToken ct=default) =>
        Write(actor,PermissionCodes.SetupManage,ct,async () =>
        {
            StockCalculation.Exact(price,8);
            if(price<0 || !await db.Currencies.AnyAsync(x=>x.Id==currencyId && x.IsActive,ct))throw new ArgumentException("Invalid price or currency.");
            await sql.ExecuteAsync("""
                UPDATE dbo.StockUnitPrices SET Price=@v WHERE ProductId=@p AND UnitId=@u AND CurrencyId=@c AND Tier=@t;
                IF @@ROWCOUNT=0 INSERT dbo.StockUnitPrices VALUES(@p,@u,@c,@t,@v)
                """,ct,("@p",productId),("@u",unitId),("@c",currencyId),("@t",Name(tier,60)),("@v",price));
            await Audit(actor,"UNIT_PRICE_SET","StockProducts",productId,ct);return productId;
        });
    public async Task<decimal?> PriceAsync(int actor,int productId,int unitId,int currencyId,string tier,CancellationToken ct=default)
    {
        await authorization.DemandAsync(actor,PermissionCodes.JournalRead,ct);
        return (await Rows("SELECT Price FROM dbo.StockUnitPrices WHERE ProductId=@p AND UnitId=@u AND CurrencyId=@c AND Tier=@t",r=>(decimal?)r.GetDecimal(0),
            ct,("@p",productId),("@u",unitId),("@c",currencyId),("@t",Name(tier,60)))).SingleOrDefault();
    }
}
