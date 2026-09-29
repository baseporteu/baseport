using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Baseport.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSecrets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "ProxyToken",
                table: "_tables",
                newName: "ProxyTokenProtected");

            migrationBuilder.RenameColumn(
                name: "ClientSecret",
                table: "_oidc_providers",
                newName: "ClientSecretProtected");

            migrationBuilder.Sql("UPDATE \"_tables\" SET \"ProxyTokenProtected\" = '';");
            migrationBuilder.Sql("UPDATE \"_oidc_providers\" SET \"ClientSecretProtected\" = '';");

            migrationBuilder.CreateTable(
                name: "_secrets",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    ValueProtected = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__secrets", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX__secrets_Name",
                table: "_secrets",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "_secrets");

            migrationBuilder.RenameColumn(
                name: "ProxyTokenProtected",
                table: "_tables",
                newName: "ProxyToken");

            migrationBuilder.RenameColumn(
                name: "ClientSecretProtected",
                table: "_oidc_providers",
                newName: "ClientSecret");
        }
    }
}
