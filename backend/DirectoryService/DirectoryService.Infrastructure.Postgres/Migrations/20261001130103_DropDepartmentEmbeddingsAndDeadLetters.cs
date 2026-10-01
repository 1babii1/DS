using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace DirectoryService.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class DropDepartmentEmbeddingsAndDeadLetters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dead_letters",
                schema: "directory");

            migrationBuilder.DropTable(
                name: "department_embeddings",
                schema: "directory");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "dead_letters",
                schema: "directory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    Error = table.Column<string>(type: "text", nullable: false),
                    FailedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageKey = table.Column<string>(type: "text", nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    Topic = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dead_letters", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "department_embeddings",
                schema: "directory",
                columns: table => new
                {
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    embedding = table.Column<Vector>(type: "vector(768)", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_department_embedding", x => x.department_id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_dead_letters_FailedAt",
                schema: "directory",
                table: "dead_letters",
                column: "FailedAt");

            migrationBuilder.CreateIndex(
                name: "IX_dead_letters_MessageId",
                schema: "directory",
                table: "dead_letters",
                column: "MessageId",
                unique: true);
        }
    }
}
