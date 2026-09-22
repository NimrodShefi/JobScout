using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobScout.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAtsFeeds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "AtsCheckedAt",
                table: "Companies",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AtsKind",
                table: "Companies",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                // Existing companies must read back as AtsKind.None - an empty string is not a
                // valid enum name and would fail every query that loads a company.
                defaultValue: "None");

            migrationBuilder.AddColumn<string>(
                name: "AtsToken",
                table: "Companies",
                type: "TEXT",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AtsCheckedAt",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "AtsKind",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "AtsToken",
                table: "Companies");
        }
    }
}
