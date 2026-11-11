using Microsoft.EntityFrameworkCore.Migrations;

namespace HisabTak.Infrastructure.Persistence.Migrations;

public partial class CashierProductUpdates : Migration
{
    protected override void Up(MigrationBuilder migration) =>
        migration.Sql("""
            ALTER TRIGGER dbo.TR_StockProducts_Immutable ON dbo.StockProducts AFTER UPDATE,DELETE AS BEGIN SET NOCOUNT ON;
                IF EXISTS(SELECT 1 FROM deleted d LEFT JOIN inserted i ON d.ProductId=i.ProductId
                    WHERE i.ProductId IS NULL OR d.Code COLLATE Latin1_General_100_BIN2<>i.Code COLLATE Latin1_General_100_BIN2
                    OR d.Name COLLATE Latin1_General_100_BIN2<>i.Name COLLATE Latin1_General_100_BIN2 OR d.BaseUnitId<>i.BaseUnitId
                    OR d.TrackBatches<>i.TrackBatches OR (d.CostMethod<>i.CostMethod AND EXISTS(SELECT 1 FROM dbo.StockMovements m WHERE m.ProductId=d.ProductId)))
                    THROW 51150,'Product identity is immutable and cost method is frozen after first movement.',1;
            END
            """);

    protected override void Down(MigrationBuilder migration) =>
        migration.Sql("""
            ALTER TRIGGER dbo.TR_StockProducts_Immutable ON dbo.StockProducts AFTER UPDATE,DELETE AS BEGIN SET NOCOUNT ON;
                IF EXISTS(SELECT 1 FROM deleted d LEFT JOIN inserted i ON d.ProductId=i.ProductId
                    WHERE i.ProductId IS NULL OR d.Code COLLATE Latin1_General_100_BIN2<>i.Code COLLATE Latin1_General_100_BIN2
                    OR d.Name COLLATE Latin1_General_100_BIN2<>i.Name COLLATE Latin1_General_100_BIN2 OR d.BaseUnitId<>i.BaseUnitId
                    OR d.TrackBatches<>i.TrackBatches OR EXISTS(SELECT 1 FROM dbo.StockMovements m WHERE m.ProductId=d.ProductId))
                    THROW 51150,'Product identity is immutable and cost method is frozen after first movement.',1;
            END
            """);
}
