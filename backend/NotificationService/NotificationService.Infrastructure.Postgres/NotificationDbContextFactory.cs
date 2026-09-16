using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace NotificationService.Infrastructure.Postgres;

// See DirectoryServiceDbContextFactory (DirectoryService) for why this exists.
public class NotificationDbContextFactory : IDesignTimeDbContextFactory<NotificationDbContext>
{
    public NotificationDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<NotificationDbContext>();
        optionsBuilder.UseNpgsql("Server=localhost;Port=5432;Database=platform;User Id=postgres;Password=postgres");

        return new NotificationDbContext(optionsBuilder.Options);
    }
}
