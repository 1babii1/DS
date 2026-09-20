using AuthService.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Shared.Kafka;
using Shared.Outbox;

namespace AuthService.Infrastructure.Postgres;

public class AuthDbContext(DbContextOptions<AuthDbContext> options)
    : IdentityDbContext<Account, Role, Guid>(options), IHasDeadLetters
{
    public DbSet<DeadLetterEntry> DeadLetters => Set<DeadLetterEntry>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema("auth");

        base.OnModelCreating(builder);

        builder.Entity<Account>(entity =>
        {
            entity.ToTable("users");

            // Partial unique index: only accounts the Hire Employee saga provisioned
            // carry an EmployeeId at all, and this is what makes
            // EmployeeEventsConsumer's "already provisioned?" check race-safe under
            // at-least-once delivery, not just a query-time convenience.
            entity.HasIndex(a => a.EmployeeId)
                .IsUnique()
                .HasFilter("\"EmployeeId\" IS NOT NULL");
        });

        builder.Entity<Role>().ToTable("roles");
        builder.Entity<IdentityUserRole<Guid>>().ToTable("user_roles");
        builder.Entity<IdentityUserClaim<Guid>>().ToTable("user_claims");
        builder.Entity<IdentityUserLogin<Guid>>().ToTable("user_logins");
        builder.Entity<IdentityUserToken<Guid>>().ToTable("user_tokens");
        builder.Entity<IdentityRoleClaim<Guid>>().ToTable("role_claims");

        builder.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("outbox_messages");
            entity.HasKey(m => m.Id);

            entity.Property(m => m.Type).HasMaxLength(200).IsRequired();
            entity.Property(m => m.AggregateId).HasMaxLength(200).IsRequired();
            entity.Property(m => m.Payload).HasColumnType("jsonb").IsRequired();

            entity.HasIndex(m => m.ProcessedAt);
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

        builder.UseOpenIddict();
    }
}