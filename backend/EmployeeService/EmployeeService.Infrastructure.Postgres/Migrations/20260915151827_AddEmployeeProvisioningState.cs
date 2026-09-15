using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EmployeeService.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddEmployeeProvisioningState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_employees_status",
                schema: "employee",
                table: "employees");

            migrationBuilder.AddColumn<string>(
                name: "ProvisioningFailureReason",
                schema: "employee",
                table: "employees",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "dead_letters",
                schema: "employee",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    Topic = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    MessageKey = table.Column<string>(type: "text", nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    Error = table.Column<string>(type: "text", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    FailedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dead_letters", x => x.Id);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_employees_status",
                schema: "employee",
                table: "employees",
                sql: "\"Status\" IN ('PendingProvisioning', 'Active', 'Terminated', 'ProvisioningFailed')");

            migrationBuilder.CreateIndex(
                name: "IX_dead_letters_FailedAt",
                schema: "employee",
                table: "dead_letters",
                column: "FailedAt");

            migrationBuilder.CreateIndex(
                name: "IX_dead_letters_MessageId",
                schema: "employee",
                table: "dead_letters",
                column: "MessageId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dead_letters",
                schema: "employee");

            migrationBuilder.DropCheckConstraint(
                name: "ck_employees_status",
                schema: "employee",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "ProvisioningFailureReason",
                schema: "employee",
                table: "employees");

            migrationBuilder.AddCheckConstraint(
                name: "ck_employees_status",
                schema: "employee",
                table: "employees",
                sql: "\"Status\" IN ('Active', 'Terminated')");
        }
    }
}
