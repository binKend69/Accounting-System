using Microsoft.EntityFrameworkCore.Migrations;

namespace HisabTak.Infrastructure.Persistence.Migrations;

public partial class PosSlots : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE dbo.PosSlots (
            ActorId int NOT NULL REFERENCES dbo.Users(UserId),
            BranchId int NOT NULL REFERENCES dbo.Branches(BranchId),
            Slot int NOT NULL CHECK (Slot BETWEEN 1 AND 4),
            Version int NOT NULL,
            Payload nvarchar(max) NULL CHECK (ISJSON(Payload)=1),
            PaidVersion int NULL,
            ReceiptId bigint NULL REFERENCES dbo.StockDocuments(DocumentId),
            PRIMARY KEY (ActorId, BranchId, Slot)
        );
        """);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        IF EXISTS(SELECT 1 FROM dbo.PosSlots) THROW 51201,'POS records exist.',1;
        DROP TABLE dbo.PosSlots;
        """);
}
