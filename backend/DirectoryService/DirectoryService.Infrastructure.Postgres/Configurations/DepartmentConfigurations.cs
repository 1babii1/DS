using DirectoryService.Domain.Departments;
using DirectoryService.Domain.Departments.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shared;

namespace DirectoryService.Infrastructure.Postgres.Configurations;

public class DepartmentConfigurations : IEntityTypeConfiguration<Department>
{
    public void Configure(EntityTypeBuilder<Department> builder)
    {
        builder.ToTable("departments");

        builder.HasKey(d => d.Id).HasName("pk_department");

        builder
            .Property(d => d.Id)
            .HasConversion(d => d.Value, id => DepartmentId.FromValue(id))
            .HasColumnName("id");

        builder
            .Property(d => d.Name)
            .HasConversion(d => d.Value, value => DepartmentName.FromPersisted(value))
            .IsRequired()
            .HasMaxLength(LengthConstants.MaxDepartmentNameLength)
            .HasColumnName("name");

        builder
            .Property(d => d.Identifier)
            .HasConversion(d => d.Value, value => DepartmentIdentifier.FromPersisted(value))
            .IsRequired()
            .HasMaxLength(LengthConstants.MaxDepartmentIdentifierLength)
            .HasColumnName("identifier");

        builder.Property(d => d.Path)
            .HasConversion(d => d.Value, value => DepartmentPath.FromPersisted(value))
            .HasColumnType("ltree")
            .HasColumnName("path")
            .IsRequired();

        // Containment queries (<@, the parent/descendant lookups throughout this
        // service) need the GiST index; GiST itself has no concept of a unique index
        // (Postgres rejects "CREATE UNIQUE INDEX ... USING gist" outright), so
        // uniqueness needs a second, ordinary btree index on the same column. Both
        // calls pass an explicit name - without it, EF Core treats two HasIndex()
        // calls on the same single column as reconfiguring one index, not defining two.
        builder.HasIndex(d => d.Path, "idx_departments_path")
            .HasMethod("gist");

        // Path is built as parent.path + "." + identifier - nothing else stops two
        // children of the same parent (or two roots) being created with the same
        // identifier, which would otherwise collide on an identical path and corrupt
        // the ltree hierarchy every query and the purge job assume is unique per node.
        builder.HasIndex(d => d.Path, "ux_departments_path")
            .IsUnique()
            .HasMethod("btree");

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

        // Mirrors LocationConfigurations/PositionConfigurations: GetDepartmentByLocationHandler
        // sorts by is_active DESC, name, id on every page.
        builder.HasIndex(d => new { d.IsActive, d.Name, d.Id })
            .IsDescending(true, false, false)
            .HasDatabaseName("ix_departments_is_active_name_id");

        // GetParentDepartmentsHandler (roots) and GetChildrenLazyHandler (children) both run
        // "WHERE parent_id = X ORDER BY created_at" - the recursive query behind every
        // department-tree page load. EF's automatic FK index on parent_id alone still leaves
        // the ORDER BY to an in-memory sort; a composite index removes that sort entirely.
        builder.HasIndex(d => new { d.ParentId, d.CreatedAt })
            .HasDatabaseName("ix_departments_parent_id_created_at");

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
            .HasOne<Department>()
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