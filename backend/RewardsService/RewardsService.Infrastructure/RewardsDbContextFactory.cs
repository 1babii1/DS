using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace RewardsService.Infrastructure;

// See DirectoryServiceDbContextFactory (DirectoryService) for why this exists.
public class RewardsDbContextFactory : IDesignTimeDbContextFactory<RewardsDbContext>
{
    public RewardsDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<RewardsDbContext>();
        optionsBuilder.UseNpgsql("Server=localhost;Port=5432;Database=platform;User Id=postgres;Password=postgres");

        return new RewardsDbContext(optionsBuilder.Options);
    }
}
