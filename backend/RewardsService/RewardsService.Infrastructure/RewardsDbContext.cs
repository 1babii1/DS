using Microsoft.EntityFrameworkCore;
using RewardsService.Domain;
using Shared.Outbox;

namespace RewardsService.Infrastructure;

public class RewardsDbContext(DbContextOptions<RewardsDbContext> options) : DbContext(options)
{
    public DbSet<Wallet> Wallets => Set<Wallet>();

    public DbSet<Transaction> Transactions => Set<Transaction>();

    public DbSet<DeadLetterEntry> DeadLetters => Set<DeadLetterEntry>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema("rewards");

        builder.Entity<Wallet>(entity =>
        {
            entity.ToTable("wallets");
            entity.HasKey(e => e.EmployeeId);

            entity.Property(e => e.Balance).HasColumnType("numeric(18,2)");
        });

        builder.Entity<Transaction>(entity =>
        {
            entity.ToTable("transactions");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Amount).HasColumnType("numeric(18,2)");
            entity.Property(e => e.Reason).HasMaxLength(500).IsRequired();
            entity.Property(e => e.Source).HasConversion<string>().HasMaxLength(50);

            entity.HasIndex(e => e.EmployeeId);
            entity.HasIndex(e => e.CreatedAt);
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

        builder.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("outbox_messages");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Type).HasMaxLength(100).IsRequired();
            entity.Property(e => e.AggregateId).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Payload).HasColumnType("jsonb").IsRequired();

            entity.HasIndex(e => e.ProcessedAt);
        });
    }
}
