using Microsoft.EntityFrameworkCore.Migrations;

namespace HisabTak.Infrastructure.Persistence.Migrations;

public partial class AccountingLedger : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Ledger tables are deliberately SQL-managed; reference entities remain EF-managed.
        migrationBuilder.Sql("""
            CREATE TABLE dbo.JournalEntries (
                JournalEntryId bigint IDENTITY PRIMARY KEY,
                SourceKey uniqueidentifier NOT NULL UNIQUE,
                ContentHash char(64) NOT NULL,
                BranchId int NOT NULL REFERENCES dbo.Branches(BranchId),
                FiscalPeriodId int NOT NULL REFERENCES dbo.FiscalPeriods(FiscalPeriodId),
                EntryDate date NOT NULL,
                Kind int NOT NULL CHECK(Kind IN (1,2,3)),
                ReversesEntryId bigint NULL REFERENCES dbo.JournalEntries(JournalEntryId),
                CreatedBy int NOT NULL REFERENCES dbo.Users(UserId),
                CreatedAt datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
                PostedAt datetime2 NULL,
                CHECK ((Kind=3 AND ReversesEntryId IS NOT NULL) OR (Kind<>3 AND ReversesEntryId IS NULL))
            );
            CREATE UNIQUE INDEX UX_JournalEntries_Reversal ON dbo.JournalEntries(ReversesEntryId) WHERE ReversesEntryId IS NOT NULL;
            CREATE TABLE dbo.JournalLines (
                JournalEntryId bigint NOT NULL REFERENCES dbo.JournalEntries(JournalEntryId),
                LineNumber int NOT NULL CHECK(LineNumber>0),
                AccountId int NOT NULL REFERENCES dbo.Accounts(AccountId),
                Side int NOT NULL CHECK(Side IN (1,2)),
                Amount decimal(28,8) NOT NULL CHECK(Amount>0),
                CurrencyId int NOT NULL REFERENCES dbo.Currencies(CurrencyId),
                RateToBase decimal(28,12) NOT NULL CHECK(RateToBase>0),
                BaseAmount decimal(28,8) NOT NULL CHECK(BaseAmount>0),
                BaseCurrencyId int NOT NULL REFERENCES dbo.Currencies(CurrencyId),
                ValuationDate date NOT NULL,
                PRIMARY KEY(JournalEntryId,LineNumber)
            );
            CREATE TABLE dbo.JournalOpeningAccounts (
                BranchId int NOT NULL REFERENCES dbo.Branches(BranchId),
                AccountId int NOT NULL REFERENCES dbo.Accounts(AccountId),
                JournalEntryId bigint NOT NULL REFERENCES dbo.JournalEntries(JournalEntryId),
                PRIMARY KEY(BranchId,AccountId)
            );
            CREATE INDEX IX_JournalEntries_Date ON dbo.JournalEntries(BranchId,EntryDate);
            CREATE INDEX IX_JournalLines_Account ON dbo.JournalLines(AccountId,CurrencyId);
            """);
        migrationBuilder.Sql("""
            CREATE TRIGGER dbo.TR_JournalEntries_Guard ON dbo.JournalEntries AFTER INSERT,UPDATE,DELETE AS
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
        migrationBuilder.Sql("""
            CREATE TRIGGER dbo.TR_JournalLines_Immutable ON dbo.JournalLines AFTER INSERT,UPDATE,DELETE AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS(SELECT 1 FROM dbo.JournalEntries e JOIN
                    (SELECT JournalEntryId FROM inserted UNION SELECT JournalEntryId FROM deleted) x ON x.JournalEntryId=e.JournalEntryId
                    WHERE e.PostedAt IS NOT NULL)
                    THROW 51104, 'Posted journal lines are immutable.', 1;
            END
            """);
        migrationBuilder.Sql("""
            CREATE TRIGGER dbo.TR_Accounts_History ON dbo.Accounts AFTER UPDATE AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS(SELECT 1 FROM inserted i JOIN deleted d ON i.AccountId=d.AccountId
                    WHERE (d.IsSystemAccount=1 AND i.IsDeleted=1) OR
                    (EXISTS(SELECT 1 FROM dbo.JournalLines l WHERE l.AccountId=i.AccountId) AND
                     (i.CurrencyId<>d.CurrencyId OR ISNULL(i.BranchId,-1)<>ISNULL(d.BranchId,-1) OR i.Nature<>d.Nature OR i.IsDeleted<>d.IsDeleted)))
                    THROW 51105, 'Account history cannot be reclassified or deleted.', 1;
            END
            """);
        migrationBuilder.Sql("""
            CREATE TRIGGER dbo.TR_Company_BaseCurrency ON dbo.CompanyInfo AFTER UPDATE AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS(SELECT 1 FROM inserted i JOIN deleted d ON i.CompanyInfoId=d.CompanyInfoId
                    WHERE ISNULL(i.BaseCurrencyId,-1)<>ISNULL(d.BaseCurrencyId,-1))
                    AND EXISTS(SELECT 1 FROM dbo.JournalEntries WHERE PostedAt IS NOT NULL)
                    THROW 51106, 'Base currency is fixed after posting.', 1;
            END
            """);
        migrationBuilder.Sql("""
            CREATE TRIGGER dbo.TR_AccountGroups_Cycles ON dbo.AccountMainGroups AFTER INSERT,UPDATE AS
            BEGIN
                SET NOCOUNT ON;
                DECLARE @limit int=(SELECT COUNT(*) FROM dbo.AccountMainGroups), @cycle bit=0;
                WITH Walk AS (
                    SELECT MainGroupId Root, ParentMainGroupId ParentId, 0 Depth FROM inserted
                    UNION ALL
                    SELECT w.Root,p.ParentMainGroupId,w.Depth+1 FROM Walk w
                    JOIN dbo.AccountMainGroups p WITH(UPDLOCK,HOLDLOCK) ON p.MainGroupId=w.ParentId
                    WHERE w.Depth<@limit
                )
                SELECT @cycle=1 FROM Walk WHERE Root=ParentId OR Depth=@limit OPTION(MAXRECURSION 0);
                IF @cycle=1 THROW 51107, 'Account group cycles are not allowed.', 1;
            END
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            IF EXISTS(SELECT 1 FROM dbo.JournalEntries)
                THROW 51108, 'Cannot downgrade a ledger containing journals.', 1;
            DROP TRIGGER dbo.TR_AccountGroups_Cycles;
            DROP TRIGGER dbo.TR_Company_BaseCurrency;
            DROP TRIGGER dbo.TR_Accounts_History;
            DROP TABLE dbo.JournalOpeningAccounts;
            DROP TABLE dbo.JournalLines;
            DROP TABLE dbo.JournalEntries;
            """);
    }
}
