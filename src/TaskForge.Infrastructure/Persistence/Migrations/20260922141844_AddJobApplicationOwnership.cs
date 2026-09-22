using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskForge.Infrastructure.Persistence.Migrations
{
    public partial class AddJobApplicationOwnership : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Jobs_IdempotencyKey",
                table: "Jobs");

            migrationBuilder.AddColumn<string>(
                name: "ApplicationId",
                table: "Jobs",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.Sql("UPDATE [Jobs] SET [ApplicationId] = N'legacy' WHERE [ApplicationId] IS NULL;");

            migrationBuilder.AlterColumn<string>(
                name: "ApplicationId",
                table: "Jobs",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                collation: "Latin1_General_100_BIN2",
                oldClrType: typeof(string),
                oldType: "nvarchar(100)",
                oldMaxLength: 100,
                oldNullable: true,
                oldCollation: "Latin1_General_100_BIN2");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_ApplicationId_IdempotencyKey",
                table: "Jobs",
                columns: new[] { "ApplicationId", "IdempotencyKey" },
                unique: true,
                filter: "[IdempotencyKey] IS NOT NULL");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Jobs_ApplicationId_IdempotencyKey",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "ApplicationId",
                table: "Jobs");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_IdempotencyKey",
                table: "Jobs",
                column: "IdempotencyKey",
                unique: true,
                filter: "[IdempotencyKey] IS NOT NULL");
        }
    }
}
