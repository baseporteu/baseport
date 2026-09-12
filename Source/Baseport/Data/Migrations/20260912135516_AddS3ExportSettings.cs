using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Baseport.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddS3ExportSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "S3AccessKey",
                table: "_settings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "S3Bucket",
                table: "_settings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "S3ExportEnabled",
                table: "_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "S3Prefix",
                table: "_settings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "S3Region",
                table: "_settings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "S3SecretKeyProtected",
                table: "_settings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "S3ServiceUrl",
                table: "_settings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "S3AccessKey",
                table: "_settings");

            migrationBuilder.DropColumn(
                name: "S3Bucket",
                table: "_settings");

            migrationBuilder.DropColumn(
                name: "S3ExportEnabled",
                table: "_settings");

            migrationBuilder.DropColumn(
                name: "S3Prefix",
                table: "_settings");

            migrationBuilder.DropColumn(
                name: "S3Region",
                table: "_settings");

            migrationBuilder.DropColumn(
                name: "S3SecretKeyProtected",
                table: "_settings");

            migrationBuilder.DropColumn(
                name: "S3ServiceUrl",
                table: "_settings");
        }
    }
}
