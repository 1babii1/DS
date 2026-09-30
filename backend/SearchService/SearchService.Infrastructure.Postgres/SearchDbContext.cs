using Microsoft.EntityFrameworkCore;
using SearchService.Domain;
using SearchService.Infrastructure.Postgres.Embeddings;
using Shared.Kafka;

namespace SearchService.Infrastructure.Postgres;

public class SearchDbContext(DbContextOptions<SearchDbContext> options) : DbContext(options), IHasDeadLetters
{
    public DbSet<DeadLetterEntry> DeadLetters => Set<DeadLetterEntry>();

    public DbSet<DocumentEmbedding> DocumentEmbeddings => Set<DocumentEmbedding>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema("search");

        builder.Entity<DocumentEmbedding>(entity =>
        {
            entity.ToTable("document_embeddings");
            entity.HasKey(e => e.DocumentId);

            entity.Property(e => e.DocumentId).HasMaxLength(100);
            entity.Property(e => e.Kind).HasMaxLength(20).IsRequired();
            entity.Property(e => e.Title).IsRequired();
            entity.Property(e => e.Text).IsRequired();
            entity.Property(e => e.TextHash).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Embedding).HasColumnType("vector(768)");

            // Semantic search orders by cosine distance; the index is what lets a LIMIT be pushed into it.
            entity.HasIndex(e => e.Embedding)
                .HasMethod("hnsw")
                .HasOperators("vector_cosine_ops");
            entity.HasIndex(e => e.Kind);
        });

        builder.Entity<DeadLetterEntry>(entity =>
        {
            entity.ToTable("dead_letters");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Topic).HasMaxLength(200).IsRequired();
            entity.Property(e => e.MessageKey).IsRequired();
            entity.Property(e => e.Payload).HasColumnType("jsonb").IsRequired();
            entity.Property(e => e.Error).IsRequired();

            entity.HasIndex(e => e.MessageId).IsUnique();
            entity.HasIndex(e => e.FailedAt);
        });
    }
}
