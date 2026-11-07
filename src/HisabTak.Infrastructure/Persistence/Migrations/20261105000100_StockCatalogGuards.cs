using Microsoft.EntityFrameworkCore.Migrations;

namespace HisabTak.Infrastructure.Persistence.Migrations;

public partial class StockCatalogGuards : Migration
{
    protected override void Up(MigrationBuilder m)
    {
        m.Sql("""
            ALTER TRIGGER dbo.TR_StockProducts_Immutable ON dbo.StockProducts AFTER UPDATE,DELETE AS BEGIN SET NOCOUNT ON;
                IF EXISTS(SELECT 1 FROM deleted d LEFT JOIN inserted i ON d.ProductId=i.ProductId
                    WHERE i.ProductId IS NULL OR d.Code COLLATE Latin1_General_100_BIN2<>i.Code COLLATE Latin1_General_100_BIN2
                    OR d.Name COLLATE Latin1_General_100_BIN2<>i.Name COLLATE Latin1_General_100_BIN2 OR d.BaseUnitId<>i.BaseUnitId
                    OR d.TrackBatches<>i.TrackBatches OR EXISTS(SELECT 1 FROM dbo.StockMovements m WHERE m.ProductId=d.ProductId))
                    THROW 51150,'Product identity is immutable and cost method is frozen after first movement.',1;
            END
            """);
        m.Sql("""
            ALTER TABLE dbo.StockDocuments ADD DocumentYear AS YEAR(EntryDate) PERSISTED,
                CreditSeries AS CONVERT(int,CASE WHEN JSON_VALUE(Payload,'$.Credit')='true' THEN 1 ELSE 0 END) PERSISTED;
            CREATE UNIQUE INDEX UX_StockDocuments_Number ON dbo.StockDocuments(BranchId,Kind,CreditSeries,DocumentYear,Number) WHERE Number IS NOT NULL;
            """);
    }
    protected override void Down(MigrationBuilder m)
    {
        m.Sql("DROP INDEX UX_StockDocuments_Number ON dbo.StockDocuments; ALTER TABLE dbo.StockDocuments DROP COLUMN DocumentYear,CreditSeries;");
        m.Sql("ALTER TRIGGER dbo.TR_StockProducts_Immutable ON dbo.StockProducts AFTER UPDATE,DELETE AS BEGIN SET NOCOUNT ON; THROW 51140,'Inventory definitions are immutable.',1; END");
    }
}
