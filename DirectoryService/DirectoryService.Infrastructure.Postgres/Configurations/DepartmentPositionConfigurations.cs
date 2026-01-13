using DirectoryService.Domain.DepartmentPositions;
using DirectoryService.Domain.DepartmentPositions.ValueObjects;
using DirectoryService.Domain.Departments.ValueObjects;
using DirectoryService.Domain.Positions.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DirectoryService.Infrastructure.Postgres.Configurations;

public class DepartmentPositionConfigurations : IEntityTypeConfiguration<DepartmentPosition>
{
    public void Configure(EntityTypeBuilder<DepartmentPosition> builder)
    {
        builder.ToTable("department_positions");

        builder.HasKey(dp => dp.Id).HasName("pk_department_position");

        builder.Property(dp => dp.Id)
            .HasConversion(dp => dp.Value, id => DepartmentPositionsId.FromValue(id))
            .HasColumnName("id");

        builder.Property(dp => dp.DepartmentId)
            .HasConversion(dp => dp.Value, id => DepartmentId.FromValue(id))
            .HasColumnName("department_id")
            .IsRequired();

        builder.Property(dp => dp.PositionId)
            .HasConversion(p => p.Value, id => PositionId.FromValue(id))
            .HasColumnName("position_id")
            .IsRequired();
    }
}