using Microsoft.EntityFrameworkCore.Migrations;

namespace HisabTak.Infrastructure.Persistence.Migrations;

// Preserve the historical rate on the principal and its separately allocated book value.
public partial class SettlementBookBasis : Migration
{
    protected override void Up(MigrationBuilder m)
    {
        m.Sql("""
            CREATE TABLE dbo.StockBookBasis(SourceKey uniqueidentifier PRIMARY KEY,AccountId int NOT NULL REFERENCES dbo.Accounts(AccountId),
                Principal decimal(28,8) NOT NULL CHECK(Principal>0),BookValue decimal(28,8) NOT NULL CHECK(BookValue>=0),
                VoucherHash varbinary(32) NULL);
            """);
        m.Sql("CREATE TRIGGER dbo.TR_StockBookBasis_Immutable ON dbo.StockBookBasis AFTER UPDATE,DELETE AS BEGIN SET NOCOUNT ON; THROW 51151,'Historical allocation is immutable.',1; END");
        ChangeGuard(m,true);
    }
    private static void ChangeGuard(MigrationBuilder m,bool enable)
    {
        const string prior="l.BookBaseAmount<>ROUND(l.BaseAmount,i.RoundingPrecision)";
        const string allocated="l.BookBaseAmount<>COALESCE((SELECT b.BookValue FROM dbo.StockBookBasis b WHERE b.SourceKey=i.SourceKey AND b.AccountId=l.AccountId AND b.Principal=l.Amount),ROUND(l.BaseAmount,i.RoundingPrecision))";
        var old=enable?prior:allocated;
        var next=enable?allocated:prior;
        m.Sql($"""
            DECLARE @definition nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID('dbo.TR_JournalEntries_Guard'));
            IF @definition IS NULL OR CHARINDEX('{old}',@definition)=0 THROW 51152,'Journal guard differs from the expected version.',1;
            SET @definition=REPLACE(@definition,'CREATE TRIGGER','ALTER TRIGGER');
            SET @definition=REPLACE(@definition,'{old}','{next}');
            EXEC sys.sp_executesql @definition;
            """);
    }
    protected override void Down(MigrationBuilder m)
    {
        m.Sql("IF EXISTS(SELECT 1 FROM dbo.StockBookBasis) THROW 51153,'Cannot discard historical allocations.',1;");
        ChangeGuard(m,false);
        m.Sql("DROP TABLE dbo.StockBookBasis");
    }
}
