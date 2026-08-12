using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Baseport.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PostgresBindAddress",
                table: "_settings",
                type: "TEXT",
                nullable: false,
                defaultValue: "127.0.0.1");

            migrationBuilder.AddColumn<bool>(
                name: "PostgresEnabled",
                table: "_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "PostgresPort",
                table: "_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 5432);

            migrationBuilder.AddColumn<string>(
                name: "TdsBindAddress",
                table: "_settings",
                type: "TEXT",
                nullable: false,
                defaultValue: "127.0.0.1");

            migrationBuilder.AddColumn<bool>(
                name: "TdsEnabled",
                table: "_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "TdsPort",
                table: "_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1433);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PostgresBindAddress",
                table: "_settings");

            migrationBuilder.DropColumn(
                name: "PostgresEnabled",
                table: "_settings");

            migrationBuilder.DropColumn(
                name: "PostgresPort",
                table: "_settings");

            migrationBuilder.DropColumn(
                name: "TdsBindAddress",
                table: "_settings");

            migrationBuilder.DropColumn(
                name: "TdsEnabled",
                table: "_settings");

            migrationBuilder.DropColumn(
                name: "TdsPort",
                table: "_settings");
        }
    }
}
