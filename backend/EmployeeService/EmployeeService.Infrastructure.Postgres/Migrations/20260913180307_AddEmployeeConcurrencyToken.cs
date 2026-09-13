using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EmployeeService.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddEmployeeConcurrencyToken : Migration
    {
        // xmin is a system column Postgres already maintains on every row of every
        // table - there is no column to create or drop here. This migration exists
        // only so the EF model snapshot records that Employee now tracks it as a
        // concurrency token; the scaffolded AddColumn/DropColumn operations were
        // removed by hand because "xmin" is a reserved system column name and
        // Postgres refuses to create a user column with that name.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
