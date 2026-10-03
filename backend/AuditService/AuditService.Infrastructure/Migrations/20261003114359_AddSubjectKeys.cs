using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuditService.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSubjectKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "subject_keys",
                schema: "audit",
                columns: table => new
                {
                    SubjectId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    WrappedKey = table.Column<byte[]>(type: "bytea", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ErasedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_subject_keys", x => x.SubjectId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "subject_keys",
                schema: "audit");
        }
    }
}
