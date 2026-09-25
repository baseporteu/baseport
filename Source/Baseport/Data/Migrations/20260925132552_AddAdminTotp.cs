using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Baseport.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminTotp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "TotpEnabledAt",
                table: "_users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TotpLastStep",
                table: "_users",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "TotpSecretProtected",
                table: "_users",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TotpEnabledAt",
                table: "_users");

            migrationBuilder.DropColumn(
                name: "TotpLastStep",
                table: "_users");

            migrationBuilder.DropColumn(
                name: "TotpSecretProtected",
                table: "_users");
        }
    }
}
