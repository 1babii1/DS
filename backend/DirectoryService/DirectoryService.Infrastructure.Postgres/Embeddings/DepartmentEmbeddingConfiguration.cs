using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DirectoryService.Infrastructure.Postgres.Embeddings;

public class DepartmentEmbeddingConfiguration : IEntityTypeConfiguration<DepartmentEmbedding>
{
    public void Configure(EntityTypeBuilder<DepartmentEmbedding> builder)
    {
        builder.ToTable("department_embeddings");

        builder.HasKey(e => e.DepartmentId).HasName("pk_department_embedding");

        builder.Property(e => e.DepartmentId).HasColumnName("department_id");

        builder.Property(e => e.Embedding).HasColumnType("vector(768)").HasColumnName("embedding");

        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at");

        // No EF-modeled relationship to Departments: its Id is a converted value object,
        // which EF can't match against a plain Guid FK. The FK constraint is added via
        // raw SQL in the migration instead.
    }
}