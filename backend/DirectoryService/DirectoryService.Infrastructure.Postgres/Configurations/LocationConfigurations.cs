using DirectoryService.Domain;
using DirectoryService.Domain.Locations;
using DirectoryService.Domain.Locations.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shared;

namespace DirectoryService.Infrastructure.Postgres.Configurations;

public class LocationConfigurations : IEntityTypeConfiguration<Location>
{
    public void Configure(EntityTypeBuilder<Location> builder)
    {
        builder.ToTable("locations");

        builder.HasKey(l => l.Id).HasName("pk_location");

        builder.Property(l => l.Id)
            .HasConversion(l => l.Value, id => LocationId.FromValue(id))
            .HasColumnName("id");

        builder.Property(l => l.Name)
            .HasConversion(l => l.Value, value => LocationName.FromPersisted(value))
            .IsRequired()
            .HasMaxLength(LengthConstants.MaxLocationNameLength)
            .HasColumnName("name");

        builder.HasIndex(l => l.Name)
            .IsUnique()
            .HasDatabaseName("ux_locations_name");

        // GetLocationsHandler's catalogue query sorts by is_active DESC, name, id on
        // every page. Without this, Postgres has no choice but a full table scan plus an
        // in-memory sort per request - unnoticeable at a handful of rows, but it degrades
        // linearly with table size, and nothing about this endpoint's contract caps how
        // large that table gets. Declared up front rather than reactively.
        builder.HasIndex(l => new { l.IsActive, l.Name, l.Id })
            .IsDescending(true, false, false)
            .HasDatabaseName("ix_locations_is_active_name_id");

        builder.Property(l => l.Timezone)
            .HasConversion(l => l.Value, value => Timezone.FromPersisted(value))
            .IsRequired()
            .HasColumnName("timezone");

        builder.OwnsOne(l => l.Address, adressBuilder =>
        {
            adressBuilder.Property(a => a.Street)
                .IsRequired()
                .HasMaxLength(LengthConstants.MaxStreetLength)
                .HasColumnName("street");

            adressBuilder.Property(a => a.City)
                .IsRequired()
                .HasMaxLength(LengthConstants.MaxCityLength)
                .HasColumnName("city");

            adressBuilder.Property(a => a.Country)
                .IsRequired()
                .HasMaxLength(LengthConstants.MaxCountryLength)
                .HasColumnName("country");

            adressBuilder.HasIndex(a => new { a.Street, a.City, a.Country })
                .IsUnique()
                .HasDatabaseName("ux_locations_address");
        });

        builder.Property(l => l.IsActive)
            .HasColumnName("is_active");

        builder.Property(l => l.CreatedAt)
            .HasColumnName("created_at");

        builder.Property(l => l.UpdatedAt)
            .HasColumnName("updated_at");

        builder
            .Property(d => d.DeletedAt)
            .HasColumnName("deleted_at")
            .IsRequired(false);

        builder.HasMany(l => l.DepartmentLocationsList)
            .WithOne()
            .HasForeignKey(l => l.LocationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}