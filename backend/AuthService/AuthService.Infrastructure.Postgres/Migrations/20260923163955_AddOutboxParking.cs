using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthService.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboxParking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AttemptCount",
                schema: "auth",
                table: "outbox_messages",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "LastError",
                schema: "auth",
                table: "outbox_messages",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ParkedAt",
                schema: "auth",
                table: "outbox_messages",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AttemptCount",
                schema: "auth",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "LastError",
                schema: "auth",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "ParkedAt",
                schema: "auth",
                table: "outbox_messages");
        }
    }
}
