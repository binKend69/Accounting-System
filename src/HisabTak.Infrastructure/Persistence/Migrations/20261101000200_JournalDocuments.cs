using Microsoft.EntityFrameworkCore.Migrations;

namespace HisabTak.Infrastructure.Persistence.Migrations;

public partial class JournalDocuments : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE dbo.DocumentNumbers (
                BranchId int NOT NULL REFERENCES dbo.Branches(BranchId),
                TypeCode nvarchar(30) NOT NULL REFERENCES dbo.DocumentTypes(Code),
                DocumentYear int NOT NULL CHECK(DocumentYear BETWEEN 1 AND 9999),
                LastNumber bigint NOT NULL CHECK(LastNumber>0),
                PRIMARY KEY(BranchId,TypeCode,DocumentYear)
            );
            CREATE TABLE dbo.JournalDocuments (
                DocumentId bigint IDENTITY PRIMARY KEY,
                BranchId int NOT NULL REFERENCES dbo.Branches(BranchId),
                TypeCode nvarchar(30) NOT NULL REFERENCES dbo.DocumentTypes(Code) CHECK(TypeCode IN ('JOURNAL_VOUCHER','JOURNAL_OPENING')),
                EntryDate date NOT NULL,
                DocumentYear AS YEAR(EntryDate) PERSISTED,
                SourceKey uniqueidentifier NOT NULL UNIQUE,
                DocumentState int NOT NULL,
                Payload nvarchar(max) NOT NULL CHECK(ISJSON(Payload)=1),
                DocumentNumber bigint NULL CHECK(DocumentNumber>0),
                JournalEntryId bigint NULL REFERENCES dbo.JournalEntries(JournalEntryId),
                ReversalEntryId bigint NULL REFERENCES dbo.JournalEntries(JournalEntryId),
                CreatedBy int NOT NULL REFERENCES dbo.Users(UserId),
                CreatedAt datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
                CHECK((DocumentState=1 AND DocumentNumber IS NULL AND JournalEntryId IS NULL AND ReversalEntryId IS NULL)
                   OR (DocumentState=2 AND DocumentNumber IS NOT NULL AND JournalEntryId IS NOT NULL AND ReversalEntryId IS NULL)
                   OR (DocumentState=3 AND DocumentNumber IS NOT NULL AND JournalEntryId IS NOT NULL AND ReversalEntryId IS NOT NULL))
            );
            CREATE UNIQUE INDEX UX_JournalDocuments_Number ON dbo.JournalDocuments(BranchId,TypeCode,DocumentYear,DocumentNumber) WHERE DocumentNumber IS NOT NULL;
            CREATE UNIQUE INDEX UX_JournalDocuments_Journal ON dbo.JournalDocuments(JournalEntryId) WHERE JournalEntryId IS NOT NULL;
            CREATE UNIQUE INDEX UX_JournalDocuments_Reversal ON dbo.JournalDocuments(ReversalEntryId) WHERE ReversalEntryId IS NOT NULL;
            """);
        migrationBuilder.Sql("""
            CREATE TRIGGER dbo.TR_JournalDocuments_Lifecycle ON dbo.JournalDocuments AFTER INSERT,UPDATE,DELETE AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS(SELECT 1 FROM deleted d LEFT JOIN inserted i ON i.DocumentId=d.DocumentId WHERE i.DocumentId IS NULL)
                    THROW 51120,'Documents cannot be deleted.',1;
                IF EXISTS(SELECT 1 FROM inserted i LEFT JOIN deleted d ON d.DocumentId=i.DocumentId WHERE d.DocumentId IS NULL AND i.DocumentState<>1)
                    THROW 51121,'Documents must begin as drafts.',1;
                IF EXISTS(SELECT 1 FROM inserted i JOIN deleted d ON d.DocumentId=i.DocumentId WHERE
                    NOT ((d.DocumentState=1 AND i.DocumentState=2) OR (d.DocumentState=2 AND i.DocumentState=3))
                    OR i.BranchId<>d.BranchId OR i.TypeCode<>d.TypeCode OR i.EntryDate<>d.EntryDate OR i.SourceKey<>d.SourceKey
                    OR i.Payload COLLATE Latin1_General_100_BIN2<>d.Payload COLLATE Latin1_General_100_BIN2
                    OR i.CreatedBy<>d.CreatedBy OR i.CreatedAt<>d.CreatedAt
                    OR (d.DocumentState=2 AND (i.DocumentNumber<>d.DocumentNumber OR i.JournalEntryId<>d.JournalEntryId)))
                    THROW 51122,'Invalid document transition or historical change.',1;
                IF EXISTS(SELECT 1 FROM inserted i WHERE i.DocumentState>=2 AND NOT EXISTS(
                    SELECT 1 FROM dbo.JournalEntries e WHERE e.JournalEntryId=i.JournalEntryId AND e.SourceKey=i.SourceKey
                    AND e.BranchId=i.BranchId AND e.EntryDate=i.EntryDate AND e.PostedAt IS NOT NULL
                    AND e.Kind=CASE WHEN i.TypeCode='JOURNAL_OPENING' THEN 2 ELSE 1 END))
                    THROW 51123,'Document must reference its committed journal.',1;
                IF EXISTS(SELECT 1 FROM inserted i WHERE i.DocumentState=3 AND NOT EXISTS(
                    SELECT 1 FROM dbo.JournalEntries e WHERE e.JournalEntryId=i.ReversalEntryId AND e.ReversesEntryId=i.JournalEntryId AND e.PostedAt IS NOT NULL))
                    THROW 51124,'Cancellation requires the original journal reversal.',1;
            END
            """);
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            IF EXISTS(SELECT 1 FROM dbo.JournalDocuments) OR EXISTS(SELECT 1 FROM dbo.DocumentNumbers)
                THROW 51125,'Cannot discard documents or allocated numbers.',1;
            DROP TABLE dbo.JournalDocuments;
            DROP TABLE dbo.DocumentNumbers;
            """);
    }
}
