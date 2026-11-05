using Microsoft.EntityFrameworkCore.Migrations;

namespace HisabTak.Infrastructure.Persistence.Migrations;

public partial class ServiceItems : Migration
{
    protected override void Up(MigrationBuilder m)
    {
        m.Sql("""
            CREATE TABLE dbo.StockServiceItems(ProductId int PRIMARY KEY REFERENCES dbo.StockProducts(ProductId));
            ALTER TABLE dbo.StockMovements ADD BilledQuantity decimal(28,8) NULL
                CHECK(BilledQuantity IS NULL OR (BilledQuantity>0 AND Quantity=0 AND Value=0 AND BatchId IS NULL));
            """);
        m.Sql("CREATE TRIGGER dbo.TR_StockServiceItems_Immutable ON dbo.StockServiceItems AFTER UPDATE,DELETE AS BEGIN SET NOCOUNT ON; THROW 51155,'Item tracking cannot change.',1; END");
        m.Sql("""
            CREATE TRIGGER dbo.TR_StockServiceItems_Create ON dbo.StockServiceItems AFTER INSERT AS BEGIN SET NOCOUNT ON;
                IF EXISTS(SELECT 1 FROM inserted i JOIN dbo.StockProducts p ON p.ProductId=i.ProductId WHERE p.TrackBatches=1 OR
                    EXISTS(SELECT 1 FROM dbo.StockMovements m WHERE m.ProductId=i.ProductId))
                    THROW 51156,'A service cannot have batches or stock history.',1;
            END
            """);
    }
    protected override void Down(MigrationBuilder m)
    {
        m.Sql("""
            IF EXISTS(SELECT 1 FROM dbo.StockServiceItems) THROW 51157,'Cannot discard service item history.',1;
            DECLARE @c sysname=(SELECT name FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID('dbo.StockMovements') AND definition LIKE '%BilledQuantity%');
            DECLARE @statement nvarchar(max)='ALTER TABLE dbo.StockMovements DROP CONSTRAINT '+QUOTENAME(@c);
            IF @c IS NOT NULL EXEC(@statement);
            ALTER TABLE dbo.StockMovements DROP COLUMN BilledQuantity;
            DROP TABLE dbo.StockServiceItems;
            """);
    }
}
