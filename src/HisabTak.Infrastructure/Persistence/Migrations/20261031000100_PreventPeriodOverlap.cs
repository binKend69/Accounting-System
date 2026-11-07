using Microsoft.EntityFrameworkCore.Migrations;

namespace HisabTak.Infrastructure.Persistence.Migrations;

public partial class PreventPeriodOverlap : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Refuse conflicting legacy rows; never silently change customer dates.
        migrationBuilder.Sql("""
            IF EXISTS (
                SELECT 1 FROM dbo.FiscalPeriods a JOIN dbo.FiscalPeriods b
                ON a.FiscalPeriodId < b.FiscalPeriodId AND a.StartDate <= b.EndDate AND b.StartDate <= a.EndDate
                WHERE a.IsActive=1 AND a.IsDeleted=0 AND b.IsActive=1 AND b.IsDeleted=0)
                THROW 51001, 'Resolve overlapping active fiscal periods before upgrading.', 1;
            """);
        migrationBuilder.Sql("""
            CREATE TRIGGER dbo.TR_FiscalPeriods_NoOverlap ON dbo.FiscalPeriods
            AFTER INSERT, UPDATE AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (
                    SELECT 1 FROM inserted i
                    JOIN dbo.FiscalPeriods p WITH (UPDLOCK, HOLDLOCK)
                    ON i.FiscalPeriodId <> p.FiscalPeriodId
                    AND i.StartDate <= p.EndDate AND p.StartDate <= i.EndDate
                    WHERE i.IsActive=1 AND i.IsDeleted=0 AND p.IsActive=1 AND p.IsDeleted=0)
                    THROW 51002, 'Active fiscal periods cannot overlap.', 1;
            END
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("DROP TRIGGER dbo.TR_FiscalPeriods_NoOverlap;");
}
