using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DirectoryService.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddCatalogueSortIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_departments_parent_id",
                schema: "directory",
                table: "departments");

            migrationBuilder.CreateIndex(
                name: "ix_positions_is_active_name_id",
                schema: "directory",
                table: "positions",
                columns: new[] { "is_active", "name", "id" },
                descending: new[] { true, false, false });

            migrationBuilder.CreateIndex(
                name: "ix_locations_is_active_name_id",
                schema: "directory",
                table: "locations",
                columns: new[] { "is_active", "name", "id" },
                descending: new[] { true, false, false });

            migrationBuilder.CreateIndex(
                name: "ix_departments_is_active_name_id",
                schema: "directory",
                table: "departments",
                columns: new[] { "is_active", "name", "id" },
                descending: new[] { true, false, false });

            migrationBuilder.CreateIndex(
                name: "ix_departments_parent_id_created_at",
                schema: "directory",
                table: "departments",
                columns: new[] { "parent_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_positions_is_active_name_id",
                schema: "directory",
                table: "positions");

            migrationBuilder.DropIndex(
                name: "ix_locations_is_active_name_id",
                schema: "directory",
                table: "locations");

            migrationBuilder.DropIndex(
                name: "ix_departments_is_active_name_id",
                schema: "directory",
                table: "departments");

            migrationBuilder.DropIndex(
                name: "ix_departments_parent_id_created_at",
                schema: "directory",
                table: "departments");

            migrationBuilder.CreateIndex(
                name: "IX_departments_parent_id",
                schema: "directory",
                table: "departments",
                column: "parent_id");
        }
    }
}
