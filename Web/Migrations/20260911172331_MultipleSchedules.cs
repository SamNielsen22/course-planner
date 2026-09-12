using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Web.Migrations
{
    /// <inheritdoc />
    public partial class MultipleSchedules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_schedules",
                table: "schedules");

            migrationBuilder.AddColumn<string>(
                name: "Id",
                table: "schedules",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "schedules",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "schedules",
                type: "text",
                nullable: false,
                defaultValue: "");

            // Rows from the one-schedule days keep their contents: the old key
            // becomes the id, and each becomes its owner's "Schedule 1".
            migrationBuilder.Sql("""UPDATE schedules SET "Id" = "UserId", "Name" = 'Schedule 1', "CreatedAt" = "UpdatedAt";""");

            migrationBuilder.AddPrimaryKey(
                name: "PK_schedules",
                table: "schedules",
                column: "Id");

            migrationBuilder.CreateIndex(
                name: "IX_schedules_UserId",
                table: "schedules",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_schedules",
                table: "schedules");

            migrationBuilder.DropIndex(
                name: "IX_schedules_UserId",
                table: "schedules");

            migrationBuilder.DropColumn(
                name: "Id",
                table: "schedules");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "schedules");

            migrationBuilder.DropColumn(
                name: "Name",
                table: "schedules");

            migrationBuilder.AddPrimaryKey(
                name: "PK_schedules",
                table: "schedules",
                column: "UserId");
        }
    }
}
