using Microsoft.EntityFrameworkCore.Migrations;

namespace HisabTak.Infrastructure.Persistence.Migrations;

public partial class CustomsFees : Migration
{
    protected override void Up(MigrationBuilder migration) => migration.Sql("""
        CREATE TABLE dbo.CustomsFeeRules(BranchId int NOT NULL REFERENCES dbo.Branches(BranchId),
            AccountId int NOT NULL REFERENCES dbo.Accounts(AccountId),Treatment int NOT NULL CHECK(Treatment BETWEEN 1 AND 4),
            PRIMARY KEY(BranchId,AccountId));
        ALTER TABLE dbo.StockDocuments DROP CONSTRAINT CK_StockDocs_ClearanceKind;
        ALTER TABLE dbo.StockDocuments ADD CONSTRAINT CK_StockDocs_FeeKind CHECK(Kind BETWEEN 1 AND 11 AND (Kind NOT IN(9,10) OR State=1));
        """);

    protected override void Down(MigrationBuilder migration) => migration.Sql("""
        IF EXISTS(SELECT 1 FROM dbo.CustomsFeeRules) OR EXISTS(SELECT 1 FROM dbo.StockDocuments WHERE Kind=11 AND State<>1)
            THROW 51207,'Cannot discard customs accounting data.',1;
        DROP TABLE dbo.CustomsFeeRules;
        ALTER TABLE dbo.StockDocuments DROP CONSTRAINT CK_StockDocs_FeeKind;
        ALTER TABLE dbo.StockDocuments ADD CONSTRAINT CK_StockDocs_ClearanceKind CHECK(Kind BETWEEN 1 AND 11 AND (Kind<9 OR State=1));
        """);
}
