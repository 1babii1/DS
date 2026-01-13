using DirectoryService.Domain.DepartmentLocations;
using DirectoryService.Domain.DepartmentLocations.ValueObjects;
using DirectoryService.Domain.Departments.ValueObjects;
using DirectoryService.Domain.Locations.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DirectoryService.Infrastructure.Postgres.Configurations;

public class DepartmentLocationConfigurations : IEntityTypeConfiguration<DepartmentLocation>
{
    public void Configure(EntityTypeBuilder<DepartmentLocation> builder)
    {
        builder.ToTable("department_locations");

        builder.HasKey(d => d.Id).HasName("pk_department_location");

        builder.Property(d => d.Id)
            .HasConversion(d => d.Value, id => DepartmentLocationsId.FromValue(id))
            .HasColumnName("id");

        builder.Property(dl => dl.LocationId)
            .HasConversion(dl => dl.Value, id => LocationId.FromValue(id))
            .HasColumnName("location_id")
            .IsRequired();

        builder.Property(dl => dl.DepartmentId)
            .HasConversion(d => d.Value, id => DepartmentId.FromValue(id))
            .HasColumnName("department_id")
            .IsRequired();
    }
}