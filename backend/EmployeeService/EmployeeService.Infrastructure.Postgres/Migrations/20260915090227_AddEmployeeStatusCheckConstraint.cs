using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EmployeeService.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddEmployeeStatusCheckConstraint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "ck_employees_status",
                schema: "employee",
                table: "employees",
                sql: "\"Status\" IN ('Active', 'Terminated')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_employees_status",
                schema: "employee",
                table: "employees");
        }
    }
}
