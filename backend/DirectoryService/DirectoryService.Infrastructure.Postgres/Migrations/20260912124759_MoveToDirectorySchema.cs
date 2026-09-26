using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DirectoryService.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class MoveToDirectorySchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "directory");

            migrationBuilder.RenameTable(
                name: "positions",
                newName: "positions",
                newSchema: "directory");

            migrationBuilder.RenameTable(
                name: "locations",
                newName: "locations",
                newSchema: "directory");

            migrationBuilder.RenameTable(
                name: "departments",
                newName: "departments",
                newSchema: "directory");

            migrationBuilder.RenameTable(
                name: "department_positions",
                newName: "department_positions",
                newSchema: "directory");

            migrationBuilder.RenameTable(
                name: "department_locations",
                newName: "department_locations",
                newSchema: "directory");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameTable(
                name: "positions",
                schema: "directory",
                newName: "positions");

            migrationBuilder.RenameTable(
                name: "locations",
                schema: "directory",
                newName: "locations");

            migrationBuilder.RenameTable(
                name: "departments",
                schema: "directory",
                newName: "departments");

            migrationBuilder.RenameTable(
                name: "department_positions",
                schema: "directory",
                newName: "department_positions");

            migrationBuilder.RenameTable(
                name: "department_locations",
                schema: "directory",
                newName: "department_locations");
        }
    }
}