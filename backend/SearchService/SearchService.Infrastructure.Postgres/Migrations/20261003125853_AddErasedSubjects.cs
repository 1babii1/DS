using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SearchService.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddErasedSubjects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "erased_subjects",
                schema: "search",
                columns: table => new
                {
                    SubjectId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ErasedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_erased_subjects", x => x.SubjectId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "erased_subjects",
                schema: "search");
        }
    }
}
