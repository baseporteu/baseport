using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Baseport.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFieldValidationRuleAndScale : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Scale",
                table: "_fields",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ValidationExpr",
                table: "_fields",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ValidationMessage",
                table: "_fields",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Scale",
                table: "_fields");

            migrationBuilder.DropColumn(
                name: "ValidationExpr",
                table: "_fields");

            migrationBuilder.DropColumn(
                name: "ValidationMessage",
                table: "_fields");
        }
    }
}
