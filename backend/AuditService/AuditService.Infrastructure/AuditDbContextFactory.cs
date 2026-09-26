using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AuditService.Infrastructure;

// See DirectoryServiceDbContextFactory (DirectoryService) for why this exists.
public class AuditDbContextFactory : IDesignTimeDbContextFactory<AuditDbContext>
{
    public AuditDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AuditDbContext>();
        optionsBuilder.UseNpgsql("Server=localhost;Port=5432;Database=platform;User Id=postgres;Password=postgres");

        return new AuditDbContext(optionsBuilder.Options);
    }
}