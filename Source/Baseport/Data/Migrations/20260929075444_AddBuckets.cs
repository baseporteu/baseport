using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Baseport.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBuckets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "_buckets",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    ApiEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ApiMethods = table.Column<string>(type: "TEXT", nullable: false),
                    AllowJwt = table.Column<bool>(type: "INTEGER", nullable: false),
                    MaxMegabytes = table.Column<int>(type: "INTEGER", nullable: false),
                    ContentTypes = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__buckets", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX__buckets_Name",
                table: "_buckets",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "_buckets");
        }
    }
}
