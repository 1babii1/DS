using AuditService.Domain;
using Microsoft.EntityFrameworkCore;

namespace AuditService.Infrastructure;

public class AuditDbContext(DbContextOptions<AuditDbContext> options) : DbContext(options)
{
    public DbSet<AuditEntry> Entries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema("audit");

        builder.Entity<AuditEntry>(entity =>
        {
            entity.ToTable("entries");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.SourceService).HasMaxLength(50).IsRequired();
            entity.Property(e => e.EventType).HasMaxLength(100).IsRequired();
            entity.Property(e => e.AggregateId).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Payload).HasColumnType("jsonb").IsRequired();

            entity.HasIndex(e => e.MessageId).IsUnique();
            entity.HasIndex(e => e.AggregateId);
            entity.HasIndex(e => e.OccurredAt);
        });
    }
}
