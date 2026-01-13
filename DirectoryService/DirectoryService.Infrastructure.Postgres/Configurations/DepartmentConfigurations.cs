using DirectoryService.Domain.Departments;
using DirectoryService.Domain.Departments.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shared;

namespace DirectoryService.Infrastructure.Postgres.Configurations;

public class DepartmentConfigurations : IEntityTypeConfiguration<Departments>
{
    public void Configure(EntityTypeBuilder<Departments> builder)
    {
        builder.ToTable("departments");

        builder.HasKey(d => d.Id).HasName("pk_department");

        builder
            .Property(d => d.Id)
            .HasConversion(d => d.Value, id => DepartmentId.FromValue(id))
            .HasColumnName("id");

        builder
            .Property(d => d.Name)
            .HasConversion(d => d.Value, name => DepartmentName.Create(name).Value)
            .IsRequired()
            .HasMaxLength(LenghtConstants.LENGTH150)
            .HasColumnName("name");

        builder
            .Property(d => d.Identifier)
            .HasConversion(d => d.Value, identifier => DepartmentIdentifier.Create(identifier).Value)
            .IsRequired()
            .HasMaxLength(LenghtConstants.LENGTH150)
            .HasColumnName("identifier");

        builder.Property(d => d.Path)
            .HasConversion(d => d.Value, path => DepartmentPath.Create(path).Value)
            .HasColumnType("ltree")
            .HasColumnName("path")
            .IsRequired();

        builder.HasIndex(d => d.Path)
            .HasMethod("gist")
            .HasDatabaseName("idx_departments_path");

        builder
            .Property(d => d.ParentId)
            .HasColumnName("parent_id")
            .IsRequired(false);

        builder
            .Property(d => d.Depth)
            .HasColumnName("depth");

        builder
            .Property(d => d.IsActive)
            .HasColumnName("is_active");

        builder
            .Property(d => d.CreatedAt)
            .HasColumnName("created_at");

        builder
            .Property(d => d.UpdatedAt)
            .HasColumnName("updated_at");

        builder
            .Property(d => d.DeletedAt)
            .HasColumnName("deleted_at")
            .IsRequired(false);

        builder
            .HasOne<Departments>()
            .WithMany(d => d.DepartmentsChildrenList)
            .HasForeignKey(d => d.ParentId);

        builder.HasMany(d => d.DepartmentsPositionsList)
            .WithOne()
            .HasForeignKey(d => d.DepartmentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(d => d.DepartmentsLocationsList)
            .WithOne()
            .HasForeignKey(d => d.DepartmentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}