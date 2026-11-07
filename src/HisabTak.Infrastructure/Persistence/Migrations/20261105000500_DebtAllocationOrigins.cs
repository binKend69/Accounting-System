using Microsoft.EntityFrameworkCore.Migrations;

namespace HisabTak.Infrastructure.Persistence.Migrations;

public partial class DebtAllocationOrigins : Migration
{
    protected override void Up(MigrationBuilder m)
    {
        m.Sql("""
            CREATE TABLE dbo.StockBasisOrigins(SourceKey uniqueidentifier NOT NULL REFERENCES dbo.StockBookBasis(SourceKey),
                InvoiceId bigint NOT NULL REFERENCES dbo.StockDebts(DocumentId),OffsetAmount decimal(28,8) NOT NULL CHECK(OffsetAmount>=0),
                OffsetBook decimal(28,8) NOT NULL CHECK(OffsetBook>=0),RefundAmount decimal(28,8) NOT NULL CHECK(RefundAmount>=0),
                RefundBook decimal(28,8) NOT NULL CHECK(RefundBook>=0),PRIMARY KEY(SourceKey,InvoiceId));
            """);
        m.Sql("CREATE TRIGGER dbo.TR_StockBasisOrigins_Immutable ON dbo.StockBasisOrigins AFTER UPDATE,DELETE AS BEGIN SET NOCOUNT ON; THROW 51158,'Allocation origins are immutable.',1; END");
        m.Sql("""
            CREATE TRIGGER dbo.TR_JournalEntries_DebtBasis ON dbo.JournalEntries AFTER INSERT,UPDATE AS BEGIN SET NOCOUNT ON;
                IF EXISTS(SELECT 1 FROM inserted e JOIN dbo.StockBookBasis b ON b.SourceKey=e.SourceKey WHERE e.PostedAt IS NOT NULL AND (
                    NOT EXISTS(SELECT 1 FROM dbo.StockBasisOrigins o WHERE o.SourceKey=b.SourceKey)
                    OR NOT(EXISTS(SELECT 1 FROM dbo.StockDocuments d WHERE d.SourceKey=e.SourceKey AND d.BranchId=e.BranchId AND d.State=1 AND d.Kind IN(4,5))
                        OR EXISTS(SELECT 1 FROM dbo.JournalDocuments d WHERE d.SourceKey=e.SourceKey AND d.BranchId=e.BranchId AND d.DocumentState=1 AND d.TypeCode IN('RECEIPT_VOUCHER','PAYMENT_VOUCHER')))
                    OR b.Principal<>(SELECT SUM(o.OffsetAmount+o.RefundAmount) FROM dbo.StockBasisOrigins o WHERE o.SourceKey=b.SourceKey)
                    OR b.BookValue<>(SELECT SUM(o.OffsetBook+o.RefundBook) FROM dbo.StockBasisOrigins o WHERE o.SourceKey=b.SourceKey)
                    OR (SELECT COUNT(*) FROM dbo.JournalLines l WHERE l.JournalEntryId=e.JournalEntryId AND l.AccountId=b.AccountId AND l.Amount=b.Principal AND l.BookBaseAmount=b.BookValue)<>1
                    OR EXISTS(SELECT 1 FROM dbo.StockBasisOrigins o JOIN dbo.StockDebts s ON s.DocumentId=o.InvoiceId JOIN dbo.StockDocuments h ON h.DocumentId=s.DocumentId
                        OUTER APPLY(SELECT COALESCE(SUM(a.Amount),0) Amount,COALESCE(SUM(a.BookValue),0) Book FROM dbo.StockDebtAllocations a
                            WHERE a.InvoiceId=s.DocumentId AND NOT EXISTS(SELECT 1 FROM dbo.JournalEntries r WHERE r.ReversesEntryId=a.JournalId AND r.PostedAt IS NOT NULL)) used
                        WHERE o.SourceKey=b.SourceKey AND (h.State<>2 OR h.BranchId<>e.BranchId OR s.AccountId<>b.AccountId
                            OR o.OffsetAmount>s.Amount-used.Amount OR o.OffsetBook>s.BookValue-used.Book
                            OR (o.OffsetAmount=s.Amount-used.Amount AND o.OffsetBook<>s.BookValue-used.Book)
                            OR (o.RefundAmount>0 AND NOT EXISTS(SELECT 1 FROM dbo.StockDocuments d WHERE d.SourceKey=b.SourceKey AND d.Kind IN(4,5))))))
                    ) THROW 51159,'Journal allocation must reconcile to its owning document and invoice balances.',1;
            END
            """);
    }
    protected override void Down(MigrationBuilder m)
    {
        m.Sql("""
            IF EXISTS(SELECT 1 FROM dbo.StockBasisOrigins) THROW 51160,'Cannot discard allocation evidence.',1;
            DROP TRIGGER dbo.TR_JournalEntries_DebtBasis;
            DROP TABLE dbo.StockBasisOrigins;
            """);
    }
}
