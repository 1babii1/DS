using Microsoft.EntityFrameworkCore;
using RewardsService.Domain;
using Shared.Kafka;
using Shared.Outbox;

namespace RewardsService.Infrastructure;

public class RewardsDbContext(DbContextOptions<RewardsDbContext> options) : DbContext(options), IHasDeadLetters
{
    public DbSet<Wallet> Wallets => Set<Wallet>();

    public DbSet<Transaction> Transactions => Set<Transaction>();

    public DbSet<AccountLookup> AccountLookups => Set<AccountLookup>();

    public DbSet<DeadLetterEntry> DeadLetters => Set<DeadLetterEntry>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

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

            // Partial unique index: the welcome bonus is granted at most once per employee, but
            // ManualGrant legitimately has many rows per employee, so the constraint must not
            // apply to those. This is what actually makes WelcomeBonusConsumer's "already
            // granted?" check race-safe under concurrent/redelivered processing - confirmed by
            // reproducing a 16x duplicate grant without it (same reasoning as Account.EmployeeId
            // in AuthDbContext).
            entity.HasIndex(e => e.EmployeeId, "IX_transactions_EmployeeId_WelcomeBonus")
                .IsUnique()
                .HasFilter("\"Source\" = 'WelcomeBonus'");

            entity.HasIndex(e => e.EmployeeId, "IX_transactions_EmployeeId");
            entity.HasIndex(e => e.CreatedAt);
        });

        builder.Entity<AccountLookup>(entity =>
        {
            entity.ToTable("account_lookups");
            entity.HasKey(e => e.EmployeeId);

            entity.HasIndex(e => e.AccountId).IsUnique();
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

        builder.Entity<IdempotencyRecord>(entity =>
        {
            entity.ToTable("idempotency_records");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Scope).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Key).HasMaxLength(200).IsRequired();
            entity.Property(e => e.RequestHash).HasMaxLength(64).IsRequired();

            // What makes a retry idempotent: the second insert on the same (Scope, Key)
            // fails the whole SaveChanges instead of creating a second Transaction.
            entity.HasIndex(e => new { e.Scope, e.Key }).IsUnique();
        });
    }
}
