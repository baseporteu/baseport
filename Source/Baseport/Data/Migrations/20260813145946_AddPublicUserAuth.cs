using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Baseport.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPublicUserAuth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AuthIssuer",
                table: "_settings",
                type: "TEXT",
                nullable: false,
                defaultValue: "baseport");

            migrationBuilder.AddColumn<int>(
                name: "AuthRefreshLifetimeDays",
                table: "_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<string>(
                name: "AuthSigningKey",
                table: "_settings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "AuthTokenLifetimeSec",
                table: "_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 3600);

            migrationBuilder.AddColumn<bool>(
                name: "PublicAuthEnabled",
                table: "_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "PublicRegistrationEnabled",
                table: "_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "_user_sessions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    RefreshTokenHash = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__user_sessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK__user_sessions__users_UserId",
                        column: x => x.UserId,
                        principalTable: "_users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX__user_sessions_RefreshTokenHash",
                table: "_user_sessions",
                column: "RefreshTokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX__user_sessions_UserId",
                table: "_user_sessions",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "_user_sessions");

            migrationBuilder.DropColumn(
                name: "AuthIssuer",
                table: "_settings");

            migrationBuilder.DropColumn(
                name: "AuthRefreshLifetimeDays",
                table: "_settings");

            migrationBuilder.DropColumn(
                name: "AuthSigningKey",
                table: "_settings");

            migrationBuilder.DropColumn(
                name: "AuthTokenLifetimeSec",
                table: "_settings");

            migrationBuilder.DropColumn(
                name: "PublicAuthEnabled",
                table: "_settings");

            migrationBuilder.DropColumn(
                name: "PublicRegistrationEnabled",
                table: "_settings");
        }
    }
}
