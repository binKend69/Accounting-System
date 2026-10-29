using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HisabTak.Infrastructure.Persistence.Migrations;

public partial class ProtectAuditLedger : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            EXEC(N'CREATE OR ALTER TRIGGER dbo.TR_AuditLogs_AppendOnly
            ON dbo.AuditLogs
            INSTEAD OF UPDATE, DELETE
            AS
            BEGIN
                SET NOCOUNT ON;
                THROW 51001, ''Audit records are append-only.'', 1;
            END;');
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TRIGGER IF EXISTS dbo.TR_AuditLogs_AppendOnly;");
    }
}
