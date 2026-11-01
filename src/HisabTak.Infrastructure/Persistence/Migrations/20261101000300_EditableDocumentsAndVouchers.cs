using Microsoft.EntityFrameworkCore.Migrations;

namespace HisabTak.Infrastructure.Persistence.Migrations;

public partial class EditableDocumentsAndVouchers : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE dbo.JournalDocuments ADD Version int NOT NULL CONSTRAINT DF_JournalDocuments_Version DEFAULT 1 CHECK(Version>0);
            DECLARE @constraint sysname=(SELECT name FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID('dbo.JournalDocuments') AND definition LIKE '%TypeCode%');
            IF @constraint IS NOT NULL EXEC('ALTER TABLE dbo.JournalDocuments DROP CONSTRAINT '+QUOTENAME(@constraint));
            ALTER TABLE dbo.JournalDocuments ADD CONSTRAINT CK_JournalDocuments_Types CHECK(TypeCode IN ('JOURNAL_VOUCHER','JOURNAL_OPENING','RECEIPT_VOUCHER','PAYMENT_VOUCHER'));
            """);
        migrationBuilder.Sql("""
            ALTER TRIGGER dbo.TR_JournalDocuments_Lifecycle ON dbo.JournalDocuments AFTER INSERT,UPDATE,DELETE AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS(SELECT 1 FROM deleted d LEFT JOIN inserted i ON i.DocumentId=d.DocumentId WHERE i.DocumentId IS NULL)
                    THROW 51120,'Documents cannot be deleted.',1;
                IF EXISTS(SELECT 1 FROM inserted i LEFT JOIN deleted d ON d.DocumentId=i.DocumentId WHERE d.DocumentId IS NULL AND i.DocumentState<>1)
                    THROW 51121,'Documents must begin as drafts.',1;
                IF EXISTS(SELECT 1 FROM inserted i JOIN deleted d ON d.DocumentId=i.DocumentId WHERE
                    NOT ((d.DocumentState=1 AND i.DocumentState IN (1,2)) OR (d.DocumentState=2 AND i.DocumentState=3))
                    OR i.Version<>d.Version+1
                    OR i.BranchId<>d.BranchId OR i.TypeCode<>d.TypeCode OR (d.DocumentState<>1 AND i.EntryDate<>d.EntryDate) OR i.SourceKey<>d.SourceKey
                    OR (NOT(d.DocumentState=1 AND i.DocumentState=1) AND i.Payload COLLATE Latin1_General_100_BIN2<>d.Payload COLLATE Latin1_General_100_BIN2)
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
            IF EXISTS(SELECT 1 FROM dbo.JournalDocuments) THROW 51126,'Cannot discard document versions.',1;
            """);
        migrationBuilder.Sql("""
            ALTER TRIGGER dbo.TR_JournalDocuments_Lifecycle ON dbo.JournalDocuments AFTER INSERT,UPDATE,DELETE AS
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
        migrationBuilder.Sql("""
            DECLARE @constraint sysname=(SELECT name FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID('dbo.JournalDocuments') AND definition LIKE '%Version%');
            IF @constraint IS NOT NULL EXEC('ALTER TABLE dbo.JournalDocuments DROP CONSTRAINT '+QUOTENAME(@constraint));
            ALTER TABLE dbo.JournalDocuments DROP CONSTRAINT DF_JournalDocuments_Version,CK_JournalDocuments_Types;
            ALTER TABLE dbo.JournalDocuments DROP COLUMN Version;
            ALTER TABLE dbo.JournalDocuments ADD CHECK(TypeCode IN ('JOURNAL_VOUCHER','JOURNAL_OPENING'));
            """);
    }
}
