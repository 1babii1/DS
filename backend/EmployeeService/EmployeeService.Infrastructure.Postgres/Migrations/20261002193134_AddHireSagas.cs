using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EmployeeService.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddHireSagas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "hire_sagas",
                schema: "employee",
                columns: table => new
                {
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Deadline = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AccountProvisioned = table.Column<bool>(type: "boolean", nullable: false),
                    BonusGranted = table.Column<bool>(type: "boolean", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompensationRequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompensationReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    AccountRevocationRequested = table.Column<bool>(type: "boolean", nullable: false),
                    BonusReversalRequested = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_hire_sagas", x => x.EmployeeId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_hire_sagas_State_Deadline",
                schema: "employee",
                table: "hire_sagas",
                columns: new[] { "State", "Deadline" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "hire_sagas",
                schema: "employee");
        }
    }
}
