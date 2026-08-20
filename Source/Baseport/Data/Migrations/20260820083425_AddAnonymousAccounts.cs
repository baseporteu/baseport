using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Baseport.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAnonymousAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsAnonymous",
                table: "_users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AnonymousAuthEnabled",
                table: "_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "AnonymousRetentionDays",
                table: "_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 30);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsAnonymous",
                table: "_users");

            migrationBuilder.DropColumn(
                name: "AnonymousAuthEnabled",
                table: "_settings");

            migrationBuilder.DropColumn(
                name: "AnonymousRetentionDays",
                table: "_settings");
        }
    }
}
