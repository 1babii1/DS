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

    public DbSet<PasskeyCredential> PasskeyCredentials => Set<PasskeyCredential>();

    public DbSet<AuthSession> AuthSessions => Set<AuthSession>();

    public DbSet<SigningKeyRecord> SigningKeys => Set<SigningKeyRecord>();

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
            entity.Property(m => m.LastError).HasMaxLength(OutboxMessage.MaxErrorLength);

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

        builder.Entity<PasskeyCredential>(entity =>
        {
            entity.ToTable("passkey_credentials");
            entity.HasKey(c => c.Id);

            // Global lookup key for the usernameless login flow: the client sends only
            // the FIDO2 credential id, and the account it belongs to is found from this
            // alone, before any email/username is known.
            entity.HasIndex(c => c.CredentialId).IsUnique();
            entity.HasIndex(c => c.AccountId);

            entity.Property(c => c.Name).HasMaxLength(100).IsRequired();
        });

        builder.Entity<AuthSession>(entity =>
        {
            entity.ToTable("auth_sessions");
            entity.HasKey(s => s.Id);
            entity.HasIndex(s => s.AccountId);
            entity.Property(s => s.UserAgent).HasMaxLength(500);
        });

        builder.Entity<SigningKeyRecord>(entity =>
        {
            entity.ToTable("signing_keys");
            entity.HasKey(k => k.Id);
            entity.HasIndex(k => new { k.Purpose, k.RetiredAt });
            entity.Property(k => k.KeyId).HasMaxLength(100).IsRequired();
            entity.Property(k => k.PrivateKeyPem).IsRequired();
        });

        builder.UseOpenIddict();
    }
}