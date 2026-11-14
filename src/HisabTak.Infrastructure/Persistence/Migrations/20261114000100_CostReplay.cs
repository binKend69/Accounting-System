using Microsoft.EntityFrameworkCore.Migrations;

namespace HisabTak.Infrastructure.Persistence.Migrations;

public partial class CostReplay : Migration
{
    protected override void Up(MigrationBuilder migration) => migration.Sql("""
        ALTER TABLE dbo.StockDocuments ADD BookPolicyAtPosting int NULL CONSTRAINT CK_StockDocuments_BookPolicyAtPosting CHECK(BookPolicyAtPosting IN(0,1,2));
        ALTER TABLE dbo.StockMovements ADD IsCostRevaluation bit NOT NULL CONSTRAINT DF_StockMovements_CostRevaluation DEFAULT 0;
        """);
    protected override void Down(MigrationBuilder migration) => migration.Sql("""
        IF EXISTS(SELECT 1 FROM dbo.StockDocuments)
            THROW 51208,'Cost policy snapshots and fee valuation history must be preserved.',1;
        ALTER TABLE dbo.StockMovements DROP CONSTRAINT DF_StockMovements_CostRevaluation;
        ALTER TABLE dbo.StockMovements DROP COLUMN IsCostRevaluation;
        ALTER TABLE dbo.StockDocuments DROP CONSTRAINT CK_StockDocuments_BookPolicyAtPosting;
        ALTER TABLE dbo.StockDocuments DROP COLUMN BookPolicyAtPosting;
        """);
}
