using Microsoft.EntityFrameworkCore.Migrations;

namespace HisabTak.Infrastructure.Persistence.Migrations;

public partial class ExplicitLedgerRounding : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE dbo.JournalEntries ADD RoundingPrecision int NULL CONSTRAINT CK_JournalEntries_Precision CHECK(RoundingPrecision BETWEEN 0 AND 8);
            ALTER TABLE dbo.JournalLines ADD BookBaseAmount decimal(28,8) NULL CONSTRAINT CK_JournalLines_BookAmount CHECK(BookBaseAmount>=0), IsRounding bit NOT NULL CONSTRAINT DF_JournalLines_IsRounding DEFAULT 0;
            CREATE TABLE dbo.LedgerRoundingSettings (
                BranchId int PRIMARY KEY REFERENCES dbo.Branches(BranchId),
                AccountId int NOT NULL REFERENCES dbo.Accounts(AccountId),
                Precision int NOT NULL CHECK(Precision BETWEEN 0 AND 8),
                BaseCurrencyId int NOT NULL REFERENCES dbo.Currencies(CurrencyId)
            );
            """);
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
                     OR (SELECT SUM(CASE WHEN l.Side=1 THEN COALESCE(l.BookBaseAmount,l.BaseAmount) ELSE -COALESCE(l.BookBaseAmount,l.BaseAmount) END) FROM dbo.JournalLines l WHERE l.JournalEntryId=i.JournalEntryId)<>0
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
                    OR EXISTS(SELECT LineNumber,AccountId,3-Side Side,Amount,CurrencyId,RateToBase,BaseAmount,BaseCurrencyId,ValuationDate,BookBaseAmount,IsRounding
                        FROM dbo.JournalLines WHERE JournalEntryId=i.ReversesEntryId
                        EXCEPT SELECT LineNumber,AccountId,Side,Amount,CurrencyId,RateToBase,BaseAmount,BaseCurrencyId,ValuationDate,BookBaseAmount,IsRounding
                        FROM dbo.JournalLines WHERE JournalEntryId=i.JournalEntryId)
                    OR EXISTS(SELECT LineNumber,AccountId,Side,Amount,CurrencyId,RateToBase,BaseAmount,BaseCurrencyId,ValuationDate,BookBaseAmount,IsRounding
                        FROM dbo.JournalLines WHERE JournalEntryId=i.JournalEntryId
                        EXCEPT SELECT LineNumber,AccountId,3-Side,Amount,CurrencyId,RateToBase,BaseAmount,BaseCurrencyId,ValuationDate,BookBaseAmount,IsRounding
                        FROM dbo.JournalLines WHERE JournalEntryId=i.ReversesEntryId)))
                    THROW 51109, 'Reversal must mirror the stored original.', 1;
                IF EXISTS(SELECT 1 FROM inserted i WHERE i.PostedAt IS NOT NULL AND i.Kind<>3 AND
                    (i.RoundingPrecision IS NULL OR EXISTS(SELECT 1 FROM dbo.JournalLines l WHERE l.JournalEntryId=i.JournalEntryId AND
                        (l.BookBaseAmount IS NULL OR l.BookBaseAmount<>ROUND(l.BaseAmount,i.RoundingPrecision)))
                     OR (SELECT SUM(CASE WHEN l.Side=1 THEN l.BaseAmount ELSE -l.BaseAmount END) FROM dbo.JournalLines l
                         WHERE l.JournalEntryId=i.JournalEntryId AND l.IsRounding=0)<>0
                     OR (SELECT COUNT(*) FROM dbo.JournalLines l WHERE l.JournalEntryId=i.JournalEntryId AND l.IsRounding=1)>1
                     OR EXISTS(SELECT 1 FROM dbo.JournalLines l WHERE l.JournalEntryId=i.JournalEntryId AND l.IsRounding=1 AND
                        (l.CurrencyId<>l.BaseCurrencyId OR l.RateToBase<>1 OR l.Amount<>l.BaseAmount OR NOT EXISTS(
                            SELECT 1 FROM dbo.LedgerRoundingSettings s WHERE s.BranchId=i.BranchId AND s.AccountId=l.AccountId
                                AND s.BaseCurrencyId=l.BaseCurrencyId AND s.Precision=i.RoundingPrecision)))))
                    THROW 51112, 'Invalid explicit rounding snapshot.', 1;
                IF EXISTS(SELECT 1 FROM inserted i JOIN dbo.JournalEntries o ON o.JournalEntryId=i.ReversesEntryId
                    WHERE i.PostedAt IS NOT NULL AND ISNULL(i.RoundingPrecision,-1)<>ISNULL(o.RoundingPrecision,-1))
                    THROW 51113, 'Reversal precision must match its original.', 1;
                IF EXISTS(SELECT 1 FROM inserted i JOIN dbo.JournalLines l ON l.JournalEntryId=i.JournalEntryId
                    WHERE i.PostedAt IS NOT NULL AND i.Kind=2 AND l.IsRounding=0 AND NOT EXISTS(
                        SELECT 1 FROM dbo.JournalOpeningAccounts o WHERE o.BranchId=i.BranchId AND o.AccountId=l.AccountId AND o.JournalEntryId=i.JournalEntryId))
                    THROW 51110, 'Opening accounts must be reserved exactly once.', 1;
            END
            """);
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            IF EXISTS(SELECT 1 FROM dbo.JournalEntries WHERE RoundingPrecision IS NOT NULL)
                THROW 51114, 'Cannot discard historical rounded values.', 1;
            """);
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
            DROP TABLE dbo.LedgerRoundingSettings;
            ALTER TABLE dbo.JournalEntries DROP CONSTRAINT CK_JournalEntries_Precision;
            ALTER TABLE dbo.JournalEntries DROP COLUMN RoundingPrecision;
            ALTER TABLE dbo.JournalLines DROP CONSTRAINT CK_JournalLines_BookAmount,DF_JournalLines_IsRounding;
            ALTER TABLE dbo.JournalLines DROP COLUMN BookBaseAmount,IsRounding;
            """);
    }
}
