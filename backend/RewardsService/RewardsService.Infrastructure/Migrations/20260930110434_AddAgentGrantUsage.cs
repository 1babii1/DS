using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RewardsService.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentGrantUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_grant_usage",
                schema: "rewards",
                columns: table => new
                {
                    GrantedByAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    Used = table.Column<decimal>(type: "numeric(18,2)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_grant_usage", x => new { x.GrantedByAccountId, x.Day });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_grant_usage",
                schema: "rewards");
        }
    }
}
