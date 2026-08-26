using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Baseport.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddActionEngine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "_action_runs",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ActionDefId = table.Column<string>(type: "TEXT", nullable: false),
                    TableId = table.Column<string>(type: "TEXT", nullable: false),
                    RecordId = table.Column<string>(type: "TEXT", nullable: false),
                    TriggerKind = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__action_runs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "_actions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    TableId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    TriggerKind = table.Column<string>(type: "TEXT", nullable: false),
                    StepsJson = table.Column<string>(type: "TEXT", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__actions", x => x.Id);
                    table.ForeignKey(
                        name: "FK__actions__tables_TableId",
                        column: x => x.TableId,
                        principalTable: "_tables",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX__action_runs_Status_NextAttemptAt",
                table: "_action_runs",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX__actions_TableId",
                table: "_actions",
                column: "TableId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "_action_runs");

            migrationBuilder.DropTable(
                name: "_actions");
        }
    }
}
