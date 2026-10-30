using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HisabTak.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SupportMerchantBranches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_CompanyInfo_Singleton",
                table: "CompanyInfo");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Branches_LocalBranch",
                table: "Branches");

            migrationBuilder.EnsureSchema(
                name: "dbo");

            migrationBuilder.CreateSequence<int>(
                name: "BranchNumbers",
                schema: "dbo");

            // Preserve existing keys and start beyond them; sequence values can have gaps.
            migrationBuilder.Sql("""
                DECLARE @nextBranchNumber bigint;
                SELECT @nextBranchNumber = COALESCE(MAX(CONVERT(bigint, [BranchId])), 0) + 1
                FROM [dbo].[Branches] WITH (TABLOCKX, HOLDLOCK);
                IF @nextBranchNumber > 2147483647
                    THROW 51003, 'Branch number capacity is exhausted.', 1;
                DECLARE @restartBranchSequence nvarchar(200) =
                    N'ALTER SEQUENCE [dbo].[BranchNumbers] RESTART WITH ' + CONVERT(nvarchar(20), @nextBranchNumber);
                EXEC(@restartBranchSequence);
                """);

            migrationBuilder.AlterColumn<int>(
                name: "BranchId",
                table: "Branches",
                type: "int",
                nullable: false,
                defaultValueSql: "NEXT VALUE FOR [dbo].[BranchNumbers]",
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CompanyInfo_Singleton",
                table: "CompanyInfo",
                sql: "[CompanyInfoId] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [dbo].[Branches] WITH (TABLOCKX, HOLDLOCK) WHERE [BranchId] <> 1)
                    OR EXISTS (SELECT 1 FROM [dbo].[CompanyInfo] WITH (TABLOCKX, HOLDLOCK) WHERE [BranchId] <> 1)
                    THROW 51002, 'Downgrade requires the original single-branch layout; no branch data was removed.', 1;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_CompanyInfo_Singleton",
                table: "CompanyInfo");

            migrationBuilder.AlterColumn<int>(
                name: "BranchId",
                table: "Branches",
                type: "int",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValueSql: "NEXT VALUE FOR [dbo].[BranchNumbers]");

            migrationBuilder.DropSequence(
                name: "BranchNumbers",
                schema: "dbo");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CompanyInfo_Singleton",
                table: "CompanyInfo",
                sql: "[CompanyInfoId] = 1 AND [BranchId] = 1");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Branches_LocalBranch",
                table: "Branches",
                sql: "[BranchId] = 1");
        }
    }
}
