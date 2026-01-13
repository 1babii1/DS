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
            .HasConversion(p => p.Value, name => PositionName.Create(name).Value)
            .IsRequired()
            .HasMaxLength(LenghtConstants.LENGTH100)
            .HasColumnName("name");

        builder.Property(p => p.Description)
            .HasConversion(p => p!.Value, description => PositionDescription.Create(description).Value)
            .HasMaxLength(LenghtConstants.LENGTH1000)
            .HasColumnName("description");

        builder.Property(p => p.IsActive)
            .HasColumnName("is_active");

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