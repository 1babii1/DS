using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EmployeeService.Infrastructure.Postgres;

// See DirectoryServiceDbContextFactory (DirectoryService) for why this exists.
public class EmployeeDbContextFactory : IDesignTimeDbContextFactory<EmployeeDbContext>
{
    public EmployeeDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<EmployeeDbContext>();
        optionsBuilder.UseNpgsql("Server=localhost;Port=5432;Database=platform;User Id=postgres;Password=postgres");

        return new EmployeeDbContext(optionsBuilder.Options);
    }
}
