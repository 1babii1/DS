using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EmployeeService.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class DropDualPublishBookkeeping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AvroExpected",
                schema: "employee",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "AvroPublishedAt",
                schema: "employee",
                table: "outbox_messages");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AvroExpected",
                schema: "employee",
                table: "outbox_messages",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "AvroPublishedAt",
                schema: "employee",
                table: "outbox_messages",
                type: "timestamp with time zone",
                nullable: true);
        }
    }
}
