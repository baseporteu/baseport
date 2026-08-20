using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Baseport.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddScheduledQueries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastResult",
                table: "_queries",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "NextRunAt",
                table: "_queries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Schedule",
                table: "_queries",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "ScheduleEnabled",
                table: "_queries",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "WebhookUrl",
                table: "_queries",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastResult",
                table: "_queries");

            migrationBuilder.DropColumn(
                name: "NextRunAt",
                table: "_queries");

            migrationBuilder.DropColumn(
                name: "Schedule",
                table: "_queries");

            migrationBuilder.DropColumn(
                name: "ScheduleEnabled",
                table: "_queries");

            migrationBuilder.DropColumn(
                name: "WebhookUrl",
                table: "_queries");
        }
    }
}
