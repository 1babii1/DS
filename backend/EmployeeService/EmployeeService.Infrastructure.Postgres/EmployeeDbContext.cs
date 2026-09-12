using EmployeeService.Application.Database;
using EmployeeService.Domain;
using Microsoft.EntityFrameworkCore;
using Shared.Outbox;

namespace EmployeeService.Infrastructure.Postgres;

public class EmployeeDbContext(DbContextOptions<EmployeeDbContext> options) : DbContext(options), IReadDbContext
{
    public DbSet<Employee> Employees => Set<Employee>();

    public IQueryable<Employee> EmployeesRead => Employees.AsNoTracking();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema("employee");

        builder.Entity<Employee>(entity =>
        {
            entity.ToTable("employees");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.FullName).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Email).HasMaxLength(200).IsRequired();
            entity.Property(e => e.DepartmentName).HasMaxLength(150).IsRequired();
            entity.Property(e => e.PositionName).HasMaxLength(150).IsRequired();
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);

            entity.HasIndex(e => e.DepartmentId);
            entity.HasIndex(e => e.Email).IsUnique();
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
    }
}
