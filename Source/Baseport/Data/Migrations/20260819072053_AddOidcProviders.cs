using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Baseport.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOidcProviders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OidcProviderId",
                table: "_users",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "OidcSubject",
                table: "_users",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "_oidc_providers",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Slug = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Authority = table.Column<string>(type: "TEXT", nullable: false),
                    ClientId = table.Column<string>(type: "TEXT", nullable: false),
                    ClientSecret = table.Column<string>(type: "TEXT", nullable: false),
                    Scopes = table.Column<string>(type: "TEXT", nullable: false),
                    UsernameClaim = table.Column<string>(type: "TEXT", nullable: false),
                    EmailClaim = table.Column<string>(type: "TEXT", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ConsoleEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    PublicEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreateAccounts = table.Column<bool>(type: "INTEGER", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__oidc_providers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX__users_OidcProviderId_OidcSubject",
                table: "_users",
                columns: new[] { "OidcProviderId", "OidcSubject" },
                unique: true,
                filter: "\"OidcSubject\" <> ''");

            migrationBuilder.CreateIndex(
                name: "IX__oidc_providers_Slug",
                table: "_oidc_providers",
                column: "Slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "_oidc_providers");

            migrationBuilder.DropIndex(
                name: "IX__users_OidcProviderId_OidcSubject",
                table: "_users");

            migrationBuilder.DropColumn(
                name: "OidcProviderId",
                table: "_users");

            migrationBuilder.DropColumn(
                name: "OidcSubject",
                table: "_users");
        }
    }
}
