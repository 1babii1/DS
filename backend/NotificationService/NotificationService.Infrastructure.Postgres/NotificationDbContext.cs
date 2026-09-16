using Microsoft.EntityFrameworkCore;
using NotificationService.Domain;

namespace NotificationService.Infrastructure.Postgres;

public class NotificationDbContext(DbContextOptions<NotificationDbContext> options) : DbContext(options)
{
    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<AccountLookup> AccountLookups => Set<AccountLookup>();

    public DbSet<DeadLetterEntry> DeadLetters => Set<DeadLetterEntry>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema("notification");

        builder.Entity<Notification>(entity =>
        {
            entity.ToTable("notifications");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Type).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Title).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Body).HasMaxLength(2000).IsRequired();
            entity.Property(e => e.DeepLink).HasMaxLength(500);

            // Every read path filters by recipient and orders by recency, and the
            // unread-count endpoint additionally filters by IsRead - covered together
            // so that query never falls back to a table scan as the log grows.
            entity.HasIndex(e => new { e.RecipientAccountId, e.CreatedAt });
            entity.HasIndex(e => new { e.RecipientAccountId, e.IsRead });
            entity.HasIndex(e => e.SourceMessageId).IsUnique();
        });

        builder.Entity<AccountLookup>(entity =>
        {
            entity.ToTable("account_lookups");
            entity.HasKey(e => e.EmployeeId);

            entity.HasIndex(e => e.AccountId);
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
