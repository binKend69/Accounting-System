using Microsoft.EntityFrameworkCore.Migrations;

namespace HisabTak.Infrastructure.Persistence.Migrations;

public partial class ProductUnitCatalog : Migration
{
    protected override void Up(MigrationBuilder m)
    {
        m.Sql("""
            CREATE SEQUENCE dbo.StockItemCodes AS bigint START WITH 1 INCREMENT BY 1;
            CREATE TABLE dbo.StockUnitBarcodes(Barcode nvarchar(80) NOT NULL PRIMARY KEY,ProductId int NOT NULL,UnitId int NOT NULL,
                UNIQUE(ProductId,UnitId),FOREIGN KEY(ProductId,UnitId) REFERENCES dbo.StockProductUnits(ProductId,UnitId));
            CREATE TABLE dbo.StockUnitPrices(ProductId int NOT NULL,UnitId int NOT NULL,CurrencyId int NOT NULL REFERENCES dbo.Currencies(CurrencyId),
                Tier nvarchar(60) NOT NULL,Price decimal(28,8) NOT NULL CHECK(Price>=0),PRIMARY KEY(ProductId,UnitId,CurrencyId,Tier),
                FOREIGN KEY(ProductId,UnitId) REFERENCES dbo.StockProductUnits(ProductId,UnitId));
            """);
    }
    protected override void Down(MigrationBuilder m)
    {
        m.Sql("""
            IF EXISTS(SELECT 1 FROM dbo.StockUnitBarcodes) OR EXISTS(SELECT 1 FROM dbo.StockUnitPrices)
                THROW 51154,'Cannot discard product catalog.',1;
            DROP TABLE dbo.StockUnitPrices,dbo.StockUnitBarcodes;
            DROP SEQUENCE dbo.StockItemCodes;
            """);
    }
}
