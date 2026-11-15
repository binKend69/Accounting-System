using Microsoft.EntityFrameworkCore.Migrations;
namespace HisabTak.Infrastructure.Persistence.Migrations;
public partial class SalesCoupons : Migration
{
    protected override void Up(MigrationBuilder migration)
    {
        migration.Sql("""
            CREATE TABLE dbo.SalesCoupons(
                CouponId bigint IDENTITY PRIMARY KEY,BranchId int NOT NULL REFERENCES dbo.Branches(BranchId),
                Code nvarchar(40) COLLATE Latin1_General_100_BIN2 NOT NULL,CurrencyId int NOT NULL REFERENCES dbo.Currencies(CurrencyId),
                Percentage bit NOT NULL,Value decimal(28,8) NOT NULL CHECK(Value>0),MinimumAmount decimal(28,8) NOT NULL CHECK(MinimumAmount>=0),
                StartsOn date NOT NULL,EndsOn date NOT NULL,MaximumUses int NOT NULL CHECK(MaximumUses>0),
                CustomerAccountId int NULL REFERENCES dbo.Accounts(AccountId),Enabled bit NOT NULL DEFAULT 1,
                UNIQUE(BranchId,Code),CHECK(StartsOn<=EndsOn),CHECK(Percentage=0 OR Value<100));
            CREATE TABLE dbo.CouponRedemptions(CouponId bigint NOT NULL REFERENCES dbo.SalesCoupons(CouponId),
                DocumentId bigint PRIMARY KEY REFERENCES dbo.StockDocuments(DocumentId));
            CREATE INDEX IX_CouponRedemptions_Coupon ON dbo.CouponRedemptions(CouponId);
            """);
        migration.Sql("""
            CREATE TRIGGER dbo.TR_CouponRedemptions_Immutable ON dbo.CouponRedemptions AFTER UPDATE,DELETE AS
            BEGIN SET NOCOUNT ON; THROW 51220,'Coupon usage history is immutable.',1; END
            """);
    }
    protected override void Down(MigrationBuilder migration)=>migration.Sql("""
        IF EXISTS(SELECT 1 FROM dbo.CouponRedemptions) OR EXISTS(SELECT 1 FROM dbo.SalesCoupons)
            THROW 51221,'Coupon definitions and usage must be preserved.',1;
        DROP TABLE dbo.CouponRedemptions; DROP TABLE dbo.SalesCoupons;
        """);
}
