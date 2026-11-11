using Microsoft.EntityFrameworkCore.Migrations;

namespace HisabTak.Infrastructure.Persistence.Migrations;

public partial class TradeIntents : Migration
{
    protected override void Up(MigrationBuilder migration)
    {
        migration.Sql("""
            DECLARE @old sysname = (SELECT name FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID('dbo.StockDocuments')
                AND (parent_column_id=COLUMNPROPERTY(OBJECT_ID('dbo.StockDocuments'),'Kind','ColumnId') OR name='CK_StockDocs_OriginalKind'));
            IF @old IS NULL THROW 51202,'Original stock kind check missing.',1;
            DECLARE @drop nvarchar(max)=N'ALTER TABLE dbo.StockDocuments DROP CONSTRAINT '+QUOTENAME(@old);
            EXEC sp_executesql @drop;
            ALTER TABLE dbo.StockDocuments ADD CONSTRAINT CK_StockDocs_TradeKind CHECK(Kind BETWEEN 1 AND 10 AND (Kind<9 OR State=1));
            ALTER TABLE dbo.StockDocuments ADD OriginIntentId bigint NULL CONSTRAINT FK_StockDocs_Intent REFERENCES dbo.StockDocuments(DocumentId);
            """);
        migration.Sql("""
            CREATE UNIQUE INDEX UX_StockDocs_Intent ON dbo.StockDocuments(OriginIntentId) WHERE OriginIntentId IS NOT NULL;
            """);
        migration.Sql("""
            CREATE TRIGGER dbo.TR_StockDocs_Intent ON dbo.StockDocuments AFTER INSERT,UPDATE AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS(SELECT 1 FROM inserted i JOIN deleted d ON i.DocumentId=d.DocumentId
                    WHERE ISNULL(i.OriginIntentId,0)<>ISNULL(d.OriginIntentId,0)
                    OR EXISTS(SELECT 1 FROM dbo.StockDocuments c WHERE c.OriginIntentId=i.DocumentId))
                    THROW 51203,'Converted intent or invoice origin cannot be edited.',1;
                IF EXISTS(SELECT 1 FROM inserted i JOIN dbo.StockDocuments p ON p.DocumentId=i.OriginIntentId
                    WHERE p.BranchId<>i.BranchId OR NOT ((p.Kind=9 AND i.Kind=3) OR (p.Kind=10 AND i.Kind=2)))
                    THROW 51204,'Invalid trade intent origin.',1;
            END
            """);
    }

    protected override void Down(MigrationBuilder migration)
    {
        migration.Sql("""
            IF EXISTS(SELECT 1 FROM dbo.StockDocuments WHERE Kind>8 OR OriginIntentId IS NOT NULL)
                THROW 51205,'Cannot discard trade intents.',1;
            DROP TRIGGER dbo.TR_StockDocs_Intent;
            DROP INDEX UX_StockDocs_Intent ON dbo.StockDocuments;
            ALTER TABLE dbo.StockDocuments DROP CONSTRAINT FK_StockDocs_Intent, CK_StockDocs_TradeKind;
            ALTER TABLE dbo.StockDocuments DROP COLUMN OriginIntentId;
            ALTER TABLE dbo.StockDocuments ADD CONSTRAINT CK_StockDocs_OriginalKind CHECK(Kind BETWEEN 1 AND 8);
            """);
    }
}
