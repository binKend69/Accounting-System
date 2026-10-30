using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HisabTak.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProtectFiscalPeriodClosure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "CK_FiscalPeriods_Closure",
                table: "FiscalPeriods",
                sql: "([IsClosed] = 0 AND [ClosedAt] IS NULL AND [ClosedByUserId] IS NULL) OR ([IsClosed] = 1 AND [ClosedAt] IS NOT NULL AND [ClosedByUserId] IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_FiscalPeriods_Closure",
                table: "FiscalPeriods");
        }
    }
}
