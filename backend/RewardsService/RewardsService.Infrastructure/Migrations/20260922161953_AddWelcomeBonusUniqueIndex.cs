using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RewardsService.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWelcomeBonusUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_transactions_EmployeeId_WelcomeBonus",
                schema: "rewards",
                table: "transactions",
                column: "EmployeeId",
                unique: true,
                filter: "\"Source\" = 'WelcomeBonus'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_transactions_EmployeeId_WelcomeBonus",
                schema: "rewards",
                table: "transactions");
        }
    }
}
