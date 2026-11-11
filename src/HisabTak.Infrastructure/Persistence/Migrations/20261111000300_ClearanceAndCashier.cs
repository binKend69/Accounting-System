using Microsoft.EntityFrameworkCore.Migrations;

namespace HisabTak.Infrastructure.Persistence.Migrations;

public partial class ClearanceAndCashier : Migration
{
    protected override void Up(MigrationBuilder migration)
    {
        migration.Sql("""
            ALTER TABLE dbo.StockDocuments DROP CONSTRAINT CK_StockDocs_TradeKind;
            ALTER TABLE dbo.StockDocuments ADD CONSTRAINT CK_StockDocs_ClearanceKind CHECK(Kind BETWEEN 1 AND 11 AND (Kind<9 OR State=1));
            ALTER TABLE dbo.StockProducts ADD DepartmentId int NULL CONSTRAINT FK_StockProduct_Department REFERENCES dbo.Departments(DepartmentId),
                PrintInKitchen bit NOT NULL CONSTRAINT DF_StockProduct_Kitchen DEFAULT 0;
            """);
    }

    protected override void Down(MigrationBuilder migration)
    {
        migration.Sql("""
            IF EXISTS(SELECT 1 FROM dbo.StockDocuments WHERE Kind=11) OR EXISTS(SELECT 1 FROM dbo.StockProducts WHERE DepartmentId IS NOT NULL OR PrintInKitchen=1)
                THROW 51206,'Cannot discard clearance or cashier data.',1;
            ALTER TABLE dbo.StockDocuments DROP CONSTRAINT CK_StockDocs_ClearanceKind;
            ALTER TABLE dbo.StockDocuments ADD CONSTRAINT CK_StockDocs_TradeKind CHECK(Kind BETWEEN 1 AND 10 AND (Kind<9 OR State=1));
            ALTER TABLE dbo.StockProducts DROP CONSTRAINT FK_StockProduct_Department, DF_StockProduct_Kitchen;
            ALTER TABLE dbo.StockProducts DROP COLUMN DepartmentId, PrintInKitchen;
            """);
    }
}
