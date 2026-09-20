using DirectoryService.Domain;
using DirectoryService.Domain.Positions;
using DirectoryService.Domain.Positions.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shared;

namespace DirectoryService.Infrastructure.Postgres.Configurations;

public class PositionConfigurations : IEntityTypeConfiguration<Position>
{
    public void Configure(EntityTypeBuilder<Position> builder)
    {
        builder.ToTable("positions");

        builder.HasKey(p => p.Id).HasName("pk_position");

        builder.Property(p => p.Id)
            .HasConversion(p => p.Value, id => PositionId.FromValue(id))
            .HasColumnName("id");

        builder.Property(p => p.Name)
            .HasConversion(p => p.Value, value => PositionName.FromPersisted(value))
            .IsRequired()
            .HasMaxLength(LengthConstants.MaxPositionNameLength)
            .HasColumnName("name");

        builder.Property(p => p.Description)
            .HasConversion(p => p!.Value, value => PositionDescription.FromPersisted(value))
            .HasMaxLength(LengthConstants.MaxPositionDescriptionLength)
            .HasColumnName("description");

        builder.Property(p => p.IsActive)
            .HasColumnName("is_active");

        // Mirrors LocationConfigurations' index: GetPositionsHandler sorts the catalogue
        // by is_active DESC, name, id on every page, same reasoning as locations.
        builder.HasIndex(p => new { p.IsActive, p.Name, p.Id })
            .IsDescending(true, false, false)
            .HasDatabaseName("ix_positions_is_active_name_id");

        builder.Property(p => p.CreatedAt)
            .HasColumnName("created_at");

        builder.Property(p => p.UpdatedAt)
            .HasColumnName("updated_at");

        builder
            .Property(d => d.DeletedAt)
            .HasColumnName("deleted_at")
            .IsRequired(false);

        builder.HasMany(p => p.DepartmentPositionsList)
            .WithOne()
            .HasForeignKey(p => p.PositionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}