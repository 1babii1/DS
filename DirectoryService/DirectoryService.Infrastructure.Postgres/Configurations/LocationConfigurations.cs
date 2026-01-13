using DirectoryService.Domain;
using DirectoryService.Domain.Locations;
using DirectoryService.Domain.Locations.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shared;

namespace DirectoryService.Infrastructure.Postgres.Configurations;

public class LocationConfigurations : IEntityTypeConfiguration<Locations>
{
    public void Configure(EntityTypeBuilder<Locations> builder)
    {
        builder.ToTable("locations");

        builder.HasKey(l => l.Id).HasName("pk_location");

        builder.Property(l => l.Id)
            .HasConversion(l => l.Value, id => LocationId.FromValue(id))
            .HasColumnName("id");

        builder.Property(l => l.Name)
            .HasConversion(l => l.Value, name => LocationName.Create(name).Value)
            .IsRequired()
            .HasMaxLength(LenghtConstants.LENGTH120)
            .HasColumnName("name");

        builder.HasIndex(l => l.Name)
            .IsUnique()
            .HasDatabaseName("ux_locations_name");

        builder.Property(l => l.Timezone)
            .HasConversion(l => l.Value, timezone => Timezone.Create(timezone).Value)
            .IsRequired()
            .HasColumnName("timezone");

        builder.OwnsOne(l => l.Address, adressBuilder =>
        {
            adressBuilder.Property(a => a.Street)
                .IsRequired()
                .HasMaxLength(LenghtConstants.LENGTH100)
                .HasColumnName("street");

            adressBuilder.Property(a => a.City)
                .IsRequired()
                .HasMaxLength(LenghtConstants.LENGTH60)
                .HasColumnName("city");

            adressBuilder.Property(a => a.Country)
                .IsRequired()
                .HasMaxLength(LenghtConstants.LENGTH60)
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