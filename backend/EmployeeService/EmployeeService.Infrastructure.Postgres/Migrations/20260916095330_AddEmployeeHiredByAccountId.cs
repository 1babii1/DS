using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EmployeeService.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddEmployeeHiredByAccountId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "HiredByAccountId",
                schema: "employee",
                table: "employees",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HiredByAccountId",
                schema: "employee",
                table: "employees");
        }
    }
}
