using AuditService.Domain;
using Microsoft.EntityFrameworkCore;
using Shared.Kafka;

namespace AuditService.Infrastructure;

public class AuditDbContext(DbContextOptions<AuditDbContext> options) : DbContext(options), IHasDeadLetters
{
    public DbSet<AuditEntry> Entries => Set<AuditEntry>();

    public DbSet<RecordedMessage> RecordedMessages => Set<RecordedMessage>();

    public DbSet<DeadLetterEntry> DeadLetters => Set<DeadLetterEntry>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema("audit");

        builder.Entity<AuditEntry>(entity =>
        {
            entity.ToTable("entries");
            // The table is partitioned by OccurredAt (ADR 0027), and a primary key on a partitioned table has to contain the
            // partition key. Uniqueness of the message id moved to RecordedMessages for the same reason.
            entity.HasKey(e => new { e.Id, e.OccurredAt });

            entity.Property(e => e.SourceService).HasMaxLength(50).IsRequired();
            entity.Property(e => e.EventType).HasMaxLength(100).IsRequired();
            entity.Property(e => e.AggregateId).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Payload).HasColumnType("jsonb").IsRequired();

            entity.HasIndex(e => e.MessageId);
            entity.HasIndex(e => e.AggregateId);
            entity.HasIndex(e => e.OccurredAt);
        });

        builder.Entity<RecordedMessage>(entity =>
        {
            entity.ToTable("recorded_messages");
            entity.HasKey(e => e.MessageId);
        });

        builder.Entity<DeadLetterEntry>(entity =>
        {
            entity.ToTable("dead_letters");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Topic).HasMaxLength(200).IsRequired();

            // Unbounded, unlike AggregateId in Entries: this table exists specifically to
            // capture messages the normal constraints reject, so it cannot carry a
            // constraint of its own that a poison message could violate the same way.
            entity.Property(e => e.MessageKey).IsRequired();
            entity.Property(e => e.Payload).HasColumnType("jsonb").IsRequired();
            entity.Property(e => e.Error).IsRequired();

            entity.HasIndex(e => e.MessageId).IsUnique();
            entity.HasIndex(e => e.FailedAt);
        });
    }
}