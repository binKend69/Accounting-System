using Microsoft.EntityFrameworkCore.Migrations;
namespace HisabTak.Infrastructure.Persistence.Migrations;
public partial class TrialBalance : Migration
{
    protected override void Up(MigrationBuilder migration)=>migration.Sql("""
        CREATE PROCEDURE dbo.ReportTrialBalance @BranchId int,@CurrencyId int=NULL,@FromDate date,@ToDate date AS
        BEGIN
            SET NOCOUNT ON;
            IF @FromDate>@ToDate THROW 51230,'Invalid reporting dates.',1;
            WITH posted AS(
                SELECT l.AccountId,l.Side,
                    CASE WHEN @CurrencyId IS NULL THEN COALESCE(l.BookBaseAmount,l.BaseAmount) ELSE l.Amount END Amount,
                    CASE WHEN e.EntryDate<@FromDate OR(e.EntryDate=@FromDate AND e.Kind=2) THEN 1 ELSE 0 END IsOpening
                FROM dbo.JournalEntries e JOIN dbo.JournalLines l ON l.JournalEntryId=e.JournalEntryId
                WHERE e.PostedAt IS NOT NULL AND e.BranchId=@BranchId AND e.EntryDate<=@ToDate
                    AND (@CurrencyId IS NULL OR l.CurrencyId=@CurrencyId)),
            totals AS(
                SELECT AccountId,SUM(CASE WHEN IsOpening=1 THEN CASE WHEN Side=1 THEN Amount ELSE -Amount END ELSE 0 END) OpeningNet,
                    SUM(CASE WHEN IsOpening=0 AND Side=1 THEN Amount ELSE 0 END) PeriodDebit,
                    SUM(CASE WHEN IsOpening=0 AND Side=2 THEN Amount ELSE 0 END) PeriodCredit
                FROM posted GROUP BY AccountId),
            balances AS(SELECT *,OpeningNet+PeriodDebit-PeriodCredit ClosingNet FROM totals)
            SELECT b.AccountId,a.AccountCode Code,a.NameAr Name,
                CASE WHEN OpeningNet>0 THEN OpeningNet ELSE 0 END OpeningDebit,
                CASE WHEN OpeningNet<0 THEN -OpeningNet ELSE 0 END OpeningCredit,
                PeriodDebit,PeriodCredit,
                CASE WHEN ClosingNet>0 THEN ClosingNet ELSE 0 END ClosingDebit,
                CASE WHEN ClosingNet<0 THEN -ClosingNet ELSE 0 END ClosingCredit
            FROM balances b JOIN dbo.Accounts a ON a.AccountId=b.AccountId ORDER BY a.AccountCode,b.AccountId;
        END
        """);
    protected override void Down(MigrationBuilder migration)=>migration.Sql("DROP PROCEDURE dbo.ReportTrialBalance;");
}
