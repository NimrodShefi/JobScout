using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobScout.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRolePreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DesiredRoles",
                table: "AppConfig",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ExcludedRoles",
                table: "AppConfig",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DesiredRoles",
                table: "AppConfig");

            migrationBuilder.DropColumn(
                name: "ExcludedRoles",
                table: "AppConfig");
        }
    }
}
