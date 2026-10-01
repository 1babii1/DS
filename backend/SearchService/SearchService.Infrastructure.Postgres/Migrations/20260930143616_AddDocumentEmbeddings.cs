using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace SearchService.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentEmbeddings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "document_embeddings",
                schema: "search",
                columns: table => new
                {
                    DocumentId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Subtitle = table.Column<string>(type: "text", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    TextHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Embedding = table.Column<Vector>(type: "vector(768)", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_embeddings", x => x.DocumentId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_document_embeddings_Embedding",
                schema: "search",
                table: "document_embeddings",
                column: "Embedding")
                .Annotation("Npgsql:IndexMethod", "hnsw")
                .Annotation("Npgsql:IndexOperators", new[] { "vector_cosine_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_document_embeddings_Kind",
                schema: "search",
                table: "document_embeddings",
                column: "Kind");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "document_embeddings",
                schema: "search");
        }
    }
}
