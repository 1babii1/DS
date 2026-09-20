using EmployeeService.Application.Database;
using EmployeeService.Domain;
using Microsoft.EntityFrameworkCore;
using Shared.Kafka;
using Shared.Outbox;

namespace EmployeeService.Infrastructure.Postgres;

public class EmployeeDbContext(DbContextOptions<EmployeeDbContext> options) : DbContext(options), IReadDbContext, IHasDeadLetters
{
    public DbSet<Employee> Employees => Set<Employee>();

    public DbSet<DeadLetterEntry> DeadLetters => Set<DeadLetterEntry>();

    public IQueryable<Employee> EmployeesRead => Employees.AsNoTracking();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema("employee");

        builder.Entity<Employee>(entity =>
        {
            // A CHECK constraint alongside the C# enum - the column is a plain
            // varchar(20) at rest (HasConversion<string>), so nothing else stops a
            // manual UPDATE or a future typo in a new enum member's string value from
            // writing a status this database now silently disagrees with.
            entity.ToTable(
                "employees",
                t => t.HasCheckConstraint(
                    "ck_employees_status",
                    "\"Status\" IN ('PendingProvisioning', 'Active', 'Terminated', 'ProvisioningFailed')"));
            entity.HasKey(e => e.Id);

            entity.Property(e => e.FullName).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Email).HasMaxLength(200).IsRequired();
            entity.Property(e => e.DepartmentName).HasMaxLength(150).IsRequired();
            entity.Property(e => e.PositionName).HasMaxLength(150).IsRequired();
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.ProvisioningFailureReason).HasMaxLength(500);

            entity.HasIndex(e => e.DepartmentId);
            entity.HasIndex(e => e.Email).IsUnique();

            // xmin is Postgres's own per-row version counter, already present on every
            // table - no new column, no backfill. Mapping it as a concurrency token means
            // SaveChanges checks it was unchanged since this row was read: two concurrent
            // transfers of the same employee now produce a lost-update conflict instead of
            // the second write silently overwriting the first.
            entity.Property<uint>("xmin").IsRowVersion();
        });

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
    }
}