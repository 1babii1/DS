using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RewardsService.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboxAvroPublishing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AvroExpected",
                schema: "rewards",
                table: "outbox_messages",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "AvroPublishedAt",
                schema: "rewards",
                table: "outbox_messages",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AvroExpected",
                schema: "rewards",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "AvroPublishedAt",
                schema: "rewards",
                table: "outbox_messages");
        }
    }
}
