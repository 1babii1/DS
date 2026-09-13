using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DirectoryService.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddDepartmentEmbeddingsHnswIndex : Migration
    {
        // Not modeled through the fluent API (no HasIndex/.HasMethod in
        // DepartmentEmbeddingConfiguration): EF's CreateIndex operation always runs inside
        // the migration's transaction, and CREATE INDEX CONCURRENTLY is not allowed inside
        // one. Raw SQL with suppressTransaction is the only way to get a non-locking index
        // build - the difference between this deploying without interrupting writes to
        // department_embeddings and holding an exclusive lock on the table for however long
        // the build takes. Left out of the model on purpose so EF never tries to "fix" it.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_department_embeddings_embedding_hnsw
                ON directory.department_embeddings
                USING hnsw (embedding vector_cosine_ops)
                """,
                suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP INDEX CONCURRENTLY IF EXISTS directory.ix_department_embeddings_embedding_hnsw",
                suppressTransaction: true);
        }
    }
}
