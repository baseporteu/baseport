using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Baseport.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "_audit_log",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Method = table.Column<string>(type: "TEXT", nullable: false),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    TableName = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__audit_log", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "_jobs",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Schedule = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    NextRunAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastRunAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastResult = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__jobs", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "_queries",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Sql = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastExecutedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__queries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "_settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AppName = table.Column<string>(type: "TEXT", nullable: false),
                    SiteUrl = table.Column<string>(type: "TEXT", nullable: false),
                    LogRetentionSec = table.Column<int>(type: "INTEGER", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", nullable: false),
                    BackupRetention = table.Column<int>(type: "INTEGER", nullable: false),
                    PreviewSecret = table.Column<string>(type: "TEXT", nullable: false),
                    AllowedOrigins = table.Column<string>(type: "TEXT", nullable: false),
                    OpenApiEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ApiTitle = table.Column<string>(type: "TEXT", nullable: false),
                    ApiDescription = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "_tables",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    IsProxy = table.Column<bool>(type: "INTEGER", nullable: false),
                    ProxyUrl = table.Column<string>(type: "TEXT", nullable: false),
                    ProxyMethod = table.Column<string>(type: "TEXT", nullable: false),
                    ProxyToken = table.Column<string>(type: "TEXT", nullable: false),
                    ProxyReadUrl = table.Column<string>(type: "TEXT", nullable: false),
                    ProxyQueryJson = table.Column<string>(type: "TEXT", nullable: false),
                    ApiEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ApiDocsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ApiName = table.Column<string>(type: "TEXT", nullable: false),
                    ApiDisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    ApiNamespace = table.Column<string>(type: "TEXT", nullable: false),
                    ApiDocumentation = table.Column<string>(type: "TEXT", nullable: false),
                    ApiMethods = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__tables", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "_users",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    Email = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Role = table.Column<string>(type: "TEXT", nullable: false),
                    IsDisabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ApiTokenHash = table.Column<string>(type: "TEXT", nullable: false),
                    ApiEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ApiTokenExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PasswordHash = table.Column<string>(type: "TEXT", nullable: false),
                    MustChangePassword = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastLoginAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "_fields",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    TableId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Label = table.Column<string>(type: "TEXT", nullable: false),
                    HelpText = table.Column<string>(type: "TEXT", nullable: false),
                    DataType = table.Column<string>(type: "TEXT", nullable: false),
                    Expression = table.Column<string>(type: "TEXT", nullable: false),
                    OptionsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Pattern = table.Column<string>(type: "TEXT", nullable: false),
                    DefaultValue = table.Column<string>(type: "TEXT", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", nullable: false),
                    Min = table.Column<double>(type: "REAL", nullable: true),
                    Max = table.Column<double>(type: "REAL", nullable: true),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    IsRequired = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsUnique = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsHidden = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsIdentifier = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsReadOnly = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__fields", x => x.Id);
                    table.ForeignKey(
                        name: "FK__fields__tables_TableId",
                        column: x => x.TableId,
                        principalTable: "_tables",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "_forms",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    TableId = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Actions = table.Column<string>(type: "TEXT", nullable: false),
                    IsReadOnly = table.Column<bool>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    LayoutJson = table.Column<string>(type: "TEXT", nullable: false),
                    ConfigJson = table.Column<string>(type: "TEXT", nullable: false),
                    IsPublished = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__forms", x => x.Id);
                    table.ForeignKey(
                        name: "FK__forms__tables_TableId",
                        column: x => x.TableId,
                        principalTable: "_tables",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "_records",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    TableId = table.Column<string>(type: "TEXT", nullable: false),
                    JsonData = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__records", x => x.Id);
                    table.ForeignKey(
                        name: "FK__records__tables_TableId",
                        column: x => x.TableId,
                        principalTable: "_tables",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX__fields_TableId",
                table: "_fields",
                column: "TableId");

            migrationBuilder.CreateIndex(
                name: "IX__forms_TableId",
                table: "_forms",
                column: "TableId");

            migrationBuilder.CreateIndex(
                name: "IX__records_TableId_CreatedAt_Id",
                table: "_records",
                columns: new[] { "TableId", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX__users_ApiTokenHash",
                table: "_users",
                column: "ApiTokenHash",
                unique: true,
                filter: "\"ApiTokenHash\" <> ''");

            migrationBuilder.CreateIndex(
                name: "IX__users_Username",
                table: "_users",
                column: "Username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "_audit_log");

            migrationBuilder.DropTable(
                name: "_fields");

            migrationBuilder.DropTable(
                name: "_forms");

            migrationBuilder.DropTable(
                name: "_jobs");

            migrationBuilder.DropTable(
                name: "_queries");

            migrationBuilder.DropTable(
                name: "_records");

            migrationBuilder.DropTable(
                name: "_settings");

            migrationBuilder.DropTable(
                name: "_users");

            migrationBuilder.DropTable(
                name: "_tables");
        }
    }
}
