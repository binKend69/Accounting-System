using Microsoft.EntityFrameworkCore.Migrations;

namespace HisabTak.Infrastructure.Persistence.Migrations;

public partial class HardenLedgerHistory : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TRIGGER dbo.TR_JournalEntries_Guard ON dbo.JournalEntries AFTER INSERT,UPDATE,DELETE AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS(SELECT 1 FROM deleted WHERE PostedAt IS NOT NULL)
                    THROW 51100, 'Posted journals are immutable.', 1;
                IF EXISTS(SELECT 1 FROM inserted i LEFT JOIN deleted d ON d.JournalEntryId=i.JournalEntryId
                          WHERE i.PostedAt IS NOT NULL AND d.JournalEntryId IS NULL)
                    THROW 51101, 'Insert journal lines before posting.', 1;
                IF EXISTS(SELECT 1 FROM inserted i WHERE i.PostedAt IS NOT NULL AND
                    (NOT EXISTS(SELECT 1 FROM dbo.FiscalPeriods p WITH(UPDLOCK,HOLDLOCK) WHERE p.FiscalPeriodId=i.FiscalPeriodId
                      AND p.IsActive=1 AND p.IsDeleted=0 AND p.IsClosed=0 AND i.EntryDate BETWEEN p.StartDate AND p.EndDate)
                     OR (SELECT COUNT(*) FROM dbo.JournalLines l WHERE l.JournalEntryId=i.JournalEntryId)<2
                     OR (SELECT SUM(CASE WHEN l.Side=1 THEN l.BaseAmount ELSE -l.BaseAmount END) FROM dbo.JournalLines l WHERE l.JournalEntryId=i.JournalEntryId)<>0
                     OR (SELECT COUNT(DISTINCT l.BaseCurrencyId) FROM dbo.JournalLines l WHERE l.JournalEntryId=i.JournalEntryId)<>1))
                    THROW 51102, 'Journal requires an open period and balanced lines.', 1;
                IF EXISTS(SELECT 1 FROM inserted i JOIN dbo.JournalLines l ON l.JournalEntryId=i.JournalEntryId
                    JOIN dbo.Accounts a WITH(HOLDLOCK) ON a.AccountId=l.AccountId
                    JOIN dbo.AccountMainGroups g WITH(HOLDLOCK) ON g.MainGroupId=a.MainGroupId
                    JOIN dbo.Currencies c WITH(HOLDLOCK) ON c.CurrencyId=l.CurrencyId
                    JOIN dbo.Currencies bc WITH(HOLDLOCK) ON bc.CurrencyId=l.BaseCurrencyId
                    JOIN dbo.Branches b WITH(HOLDLOCK) ON b.BranchId=i.BranchId
                    WHERE i.PostedAt IS NOT NULL AND (a.IsActive=0 OR a.IsDeleted=1 OR a.IsSuspended=1 OR
                        a.CurrencyId<>l.CurrencyId OR a.BranchId IS NULL OR a.BranchId<>i.BranchId OR
                        g.IsActive=0 OR g.IsDeleted=1 OR c.IsActive=0 OR c.IsDeleted=1 OR bc.IsActive=0 OR bc.IsDeleted=1 OR b.IsActive=0 OR b.IsDeleted=1 OR
                        NOT EXISTS(SELECT 1 FROM dbo.CompanyInfo co WITH(HOLDLOCK) WHERE co.IsActive=1 AND co.IsDeleted=0 AND co.BaseCurrencyId=l.BaseCurrencyId)))
                    THROW 51103, 'Journal account, branch or currency is unavailable.', 1;
                IF EXISTS(SELECT 1 FROM inserted i WHERE i.PostedAt IS NOT NULL AND i.Kind=3 AND
                    (NOT EXISTS(SELECT 1 FROM dbo.JournalEntries o WHERE o.JournalEntryId=i.ReversesEntryId
                        AND o.PostedAt IS NOT NULL AND o.Kind<>3 AND o.BranchId=i.BranchId)
                    OR EXISTS(SELECT LineNumber,AccountId,3-Side Side,Amount,CurrencyId,RateToBase,BaseAmount,BaseCurrencyId,ValuationDate
                        FROM dbo.JournalLines WHERE JournalEntryId=i.ReversesEntryId
                        EXCEPT SELECT LineNumber,AccountId,Side,Amount,CurrencyId,RateToBase,BaseAmount,BaseCurrencyId,ValuationDate
                        FROM dbo.JournalLines WHERE JournalEntryId=i.JournalEntryId)
                    OR EXISTS(SELECT LineNumber,AccountId,Side,Amount,CurrencyId,RateToBase,BaseAmount,BaseCurrencyId,ValuationDate
                        FROM dbo.JournalLines WHERE JournalEntryId=i.JournalEntryId
                        EXCEPT SELECT LineNumber,AccountId,3-Side,Amount,CurrencyId,RateToBase,BaseAmount,BaseCurrencyId,ValuationDate
                        FROM dbo.JournalLines WHERE JournalEntryId=i.ReversesEntryId)))
                    THROW 51109, 'Reversal must mirror the stored original.', 1;
                IF EXISTS(SELECT 1 FROM inserted i JOIN dbo.JournalLines l ON l.JournalEntryId=i.JournalEntryId
                    WHERE i.PostedAt IS NOT NULL AND i.Kind=2 AND NOT EXISTS(
                        SELECT 1 FROM dbo.JournalOpeningAccounts o WHERE o.BranchId=i.BranchId AND o.AccountId=l.AccountId AND o.JournalEntryId=i.JournalEntryId))
                    THROW 51110, 'Opening accounts must be reserved exactly once.', 1;
            END
            """);
        migrationBuilder.Sql("""
            CREATE TRIGGER dbo.TR_JournalOpeningAccounts_Immutable ON dbo.JournalOpeningAccounts AFTER INSERT,UPDATE,DELETE AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS(SELECT 1 FROM dbo.JournalEntries e JOIN
                    (SELECT JournalEntryId FROM inserted UNION SELECT JournalEntryId FROM deleted) x ON x.JournalEntryId=e.JournalEntryId
                    WHERE e.PostedAt IS NOT NULL)
                    THROW 51111, 'Posted opening reservations are immutable.', 1;
            END
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TRIGGER dbo.TR_JournalOpeningAccounts_Immutable");
        migrationBuilder.Sql("""
            ALTER TRIGGER dbo.TR_JournalEntries_Guard ON dbo.JournalEntries AFTER INSERT,UPDATE,DELETE AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS(SELECT 1 FROM deleted WHERE PostedAt IS NOT NULL)
                    THROW 51100, 'Posted journals are immutable.', 1;
                IF EXISTS(SELECT 1 FROM inserted i LEFT JOIN deleted d ON d.JournalEntryId=i.JournalEntryId
                          WHERE i.PostedAt IS NOT NULL AND d.JournalEntryId IS NULL)
                    THROW 51101, 'Insert journal lines before posting.', 1;
                IF EXISTS(SELECT 1 FROM inserted i WHERE i.PostedAt IS NOT NULL AND
                    (NOT EXISTS(SELECT 1 FROM dbo.FiscalPeriods p WITH(UPDLOCK,HOLDLOCK) WHERE p.FiscalPeriodId=i.FiscalPeriodId
                      AND p.IsActive=1 AND p.IsDeleted=0 AND p.IsClosed=0 AND i.EntryDate BETWEEN p.StartDate AND p.EndDate)
                     OR (SELECT COUNT(*) FROM dbo.JournalLines l WHERE l.JournalEntryId=i.JournalEntryId)<2
                     OR (SELECT SUM(CASE WHEN l.Side=1 THEN l.BaseAmount ELSE -l.BaseAmount END) FROM dbo.JournalLines l WHERE l.JournalEntryId=i.JournalEntryId)<>0
                     OR (SELECT COUNT(DISTINCT l.BaseCurrencyId) FROM dbo.JournalLines l WHERE l.JournalEntryId=i.JournalEntryId)<>1))
                    THROW 51102, 'Journal requires an open period and balanced lines.', 1;
                IF EXISTS(SELECT 1 FROM inserted i JOIN dbo.JournalLines l ON l.JournalEntryId=i.JournalEntryId
                    JOIN dbo.Accounts a ON a.AccountId=l.AccountId
                    JOIN dbo.Currencies c ON c.CurrencyId=l.CurrencyId
                    JOIN dbo.Currencies bc ON bc.CurrencyId=l.BaseCurrencyId
                    JOIN dbo.Branches b ON b.BranchId=i.BranchId
                    WHERE i.PostedAt IS NOT NULL AND (a.IsActive=0 OR a.IsDeleted=1 OR a.IsSuspended=1 OR
                        a.CurrencyId<>l.CurrencyId OR a.BranchId IS NULL OR a.BranchId<>i.BranchId OR
                        c.IsActive=0 OR c.IsDeleted=1 OR bc.IsActive=0 OR bc.IsDeleted=1 OR b.IsActive=0 OR b.IsDeleted=1 OR
                        NOT EXISTS(SELECT 1 FROM dbo.CompanyInfo co WHERE co.IsActive=1 AND co.IsDeleted=0 AND co.BaseCurrencyId=l.BaseCurrencyId)))
                    THROW 51103, 'Journal account, branch or currency is unavailable.', 1;
            END
            """);
    }
}
