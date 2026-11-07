using Microsoft.EntityFrameworkCore.Migrations;

namespace HisabTak.Infrastructure.Persistence.Migrations;

public partial class StockDocuments : Migration
{
    protected override void Up(MigrationBuilder m)
    {
        m.Sql("""
            CREATE TABLE dbo.StockProducts(ProductId int IDENTITY PRIMARY KEY,Code nvarchar(40) NOT NULL UNIQUE,Name nvarchar(200) NOT NULL,
                BaseUnitId int NOT NULL REFERENCES dbo.Units(UnitId),CostMethod int NOT NULL CHECK(CostMethod IN(1,2)),TrackBatches bit NOT NULL);
            CREATE TABLE dbo.StockProductUnits(ProductId int NOT NULL REFERENCES dbo.StockProducts(ProductId),UnitId int NOT NULL REFERENCES dbo.Units(UnitId),
                Factor decimal(28,8) NOT NULL CHECK(Factor>0),PRIMARY KEY(ProductId,UnitId));
            CREATE TABLE dbo.StockBatches(BatchId bigint IDENTITY PRIMARY KEY,ProductId int NOT NULL REFERENCES dbo.StockProducts(ProductId),
                WarehouseId int NOT NULL REFERENCES dbo.Warehouses(WarehouseId),Code nvarchar(80) NOT NULL,Expiry date NOT NULL,UNIQUE(ProductId,WarehouseId,Code));
            CREATE TABLE dbo.StockAccountSettings(BranchId int PRIMARY KEY REFERENCES dbo.Branches(BranchId),Payload nvarchar(max) NOT NULL CHECK(ISJSON(Payload)=1));
            CREATE TABLE dbo.StockDocuments(DocumentId bigint IDENTITY PRIMARY KEY,BranchId int NOT NULL REFERENCES dbo.Branches(BranchId),
                SourceKey uniqueidentifier NOT NULL UNIQUE,Kind int NOT NULL CHECK(Kind BETWEEN 1 AND 8),EntryDate date NOT NULL,
                Payload nvarchar(max) NOT NULL CHECK(ISJSON(Payload)=1),Version int NOT NULL DEFAULT 1 CHECK(Version>0),State int NOT NULL DEFAULT 1 CHECK(State IN(1,2,3)),
                Number bigint NULL,JournalId bigint NULL REFERENCES dbo.JournalEntries(JournalEntryId),ReversalId bigint NULL REFERENCES dbo.JournalEntries(JournalEntryId),
                ReversalDate date NULL,CreatedBy int NOT NULL REFERENCES dbo.Users(UserId),
                CHECK((State=1 AND Number IS NULL AND JournalId IS NULL AND ReversalId IS NULL AND ReversalDate IS NULL) OR
                    (State=2 AND Number IS NOT NULL AND JournalId IS NOT NULL AND ReversalId IS NULL AND ReversalDate IS NULL) OR
                    (State=3 AND Number IS NOT NULL AND JournalId IS NOT NULL AND ReversalId IS NOT NULL AND ReversalDate IS NOT NULL)));
            CREATE TABLE dbo.StockMovements(MovementId bigint IDENTITY PRIMARY KEY,DocumentId bigint NOT NULL REFERENCES dbo.StockDocuments(DocumentId),LineNumber int NOT NULL,
                ProductId int NOT NULL REFERENCES dbo.StockProducts(ProductId),WarehouseId int NOT NULL REFERENCES dbo.Warehouses(WarehouseId),
                BatchId bigint NULL REFERENCES dbo.StockBatches(BatchId),Quantity decimal(28,8) NOT NULL,Value decimal(28,8) NOT NULL,
                Net decimal(28,8) NOT NULL,Tax decimal(28,8) NOT NULL,LastPurchasePrice decimal(28,8) NULL,
                OriginalMovementId bigint NULL REFERENCES dbo.StockMovements(MovementId));
            CREATE INDEX IX_StockMovements_Position ON dbo.StockMovements(ProductId,WarehouseId,MovementId);
            CREATE TABLE dbo.StockDebts(DocumentId bigint PRIMARY KEY REFERENCES dbo.StockDocuments(DocumentId),AccountId int NOT NULL REFERENCES dbo.Accounts(AccountId),
                CurrencyId int NOT NULL REFERENCES dbo.Currencies(CurrencyId),Receivable bit NOT NULL,Amount decimal(28,8) NOT NULL CHECK(Amount>0),
                BookValue decimal(28,8) NOT NULL CHECK(BookValue>=0));
            CREATE TABLE dbo.StockDebtAllocations(AllocationId bigint IDENTITY PRIMARY KEY,InvoiceId bigint NOT NULL REFERENCES dbo.StockDebts(DocumentId),
                SourceKey uniqueidentifier NOT NULL UNIQUE,ReturnDocumentId bigint NULL REFERENCES dbo.StockDocuments(DocumentId),JournalId bigint NOT NULL REFERENCES dbo.JournalEntries(JournalEntryId),
                Amount decimal(28,8) NOT NULL CHECK(Amount>0),BookValue decimal(28,8) NOT NULL CHECK(BookValue>=0),Payload nvarchar(max) NOT NULL CHECK(ISJSON(Payload)=1));
            """);
        foreach (var table in new[] { "StockProducts", "StockProductUnits", "StockBatches", "StockMovements", "StockDebts", "StockDebtAllocations" })
            m.Sql($"CREATE TRIGGER dbo.TR_{table}_Immutable ON dbo.{table} AFTER UPDATE,DELETE AS BEGIN SET NOCOUNT ON; THROW 51140,'Inventory history and definitions are immutable.',1; END");
        m.Sql("""
            CREATE TRIGGER dbo.TR_StockMovements_Draft ON dbo.StockMovements AFTER INSERT AS BEGIN SET NOCOUNT ON;
                IF EXISTS(SELECT 1 FROM inserted i JOIN dbo.StockDocuments d ON d.DocumentId=i.DocumentId WHERE d.State<>1)
                    THROW 51141,'Stock effects require a draft transaction.',1;
                IF EXISTS(SELECT 1 FROM inserted i JOIN dbo.StockProducts p ON p.ProductId=i.ProductId LEFT JOIN dbo.StockBatches b ON b.BatchId=i.BatchId
                    WHERE (p.TrackBatches=1 AND i.Quantity<>0 AND i.BatchId IS NULL) OR (p.TrackBatches=0 AND i.BatchId IS NOT NULL)
                    OR (i.BatchId IS NOT NULL AND (b.ProductId<>i.ProductId OR b.WarehouseId<>i.WarehouseId)))
                    THROW 51142,'Invalid inventory batch ownership.',1;
            END
            """);
        m.Sql("""
            CREATE TRIGGER dbo.TR_StockDocuments_Lifecycle ON dbo.StockDocuments AFTER INSERT,UPDATE,DELETE AS BEGIN SET NOCOUNT ON;
                IF EXISTS(SELECT 1 FROM deleted d LEFT JOIN inserted i ON i.DocumentId=d.DocumentId WHERE i.DocumentId IS NULL)
                    THROW 51143,'Stock documents cannot be deleted.',1;
                IF EXISTS(SELECT 1 FROM inserted i LEFT JOIN deleted d ON i.DocumentId=d.DocumentId WHERE d.DocumentId IS NULL AND i.State<>1)
                    THROW 51144,'Stock documents begin as drafts.',1;
                IF EXISTS(SELECT 1 FROM inserted i JOIN deleted d ON i.DocumentId=d.DocumentId WHERE
                    i.BranchId<>d.BranchId OR i.SourceKey<>d.SourceKey OR i.Kind<>d.Kind OR i.CreatedBy<>d.CreatedBy OR i.Version<>d.Version+1
                    OR NOT((d.State=1 AND i.State IN(1,2)) OR (d.State=2 AND i.State=3))
                    OR (NOT(d.State=1 AND i.State=1) AND (i.Payload COLLATE Latin1_General_100_BIN2<>d.Payload COLLATE Latin1_General_100_BIN2 OR i.EntryDate<>d.EntryDate))
                    OR (d.State=2 AND (i.Number<>d.Number OR i.JournalId<>d.JournalId)))
                    THROW 51145,'Invalid stock document change.',1;
                IF EXISTS(SELECT 1 FROM inserted i WHERE i.State>=2 AND NOT EXISTS(SELECT 1 FROM dbo.JournalEntries j WHERE j.JournalEntryId=i.JournalId
                    AND j.SourceKey=i.SourceKey AND j.BranchId=i.BranchId AND j.EntryDate=i.EntryDate AND j.PostedAt IS NOT NULL))
                    THROW 51146,'Stock document requires its posted journal.',1;
                IF EXISTS(SELECT 1 FROM inserted i WHERE i.State=3 AND NOT EXISTS(SELECT 1 FROM dbo.JournalEntries j WHERE j.JournalEntryId=i.ReversalId
                    AND j.ReversesEntryId=i.JournalId AND j.EntryDate=i.ReversalDate AND j.PostedAt IS NOT NULL))
                    THROW 51147,'Stock cancellation requires its reversal.',1;
            END
            """);
    }
    protected override void Down(MigrationBuilder m)
    {
        m.Sql("""
            IF EXISTS(SELECT 1 FROM dbo.StockDocuments) OR EXISTS(SELECT 1 FROM dbo.StockProducts)
                THROW 51148,'Cannot discard inventory history.',1;
            DROP TABLE dbo.StockDebtAllocations,dbo.StockDebts,dbo.StockMovements,dbo.StockDocuments,dbo.StockAccountSettings,dbo.StockBatches,dbo.StockProductUnits,dbo.StockProducts;
            """);
    }
}
