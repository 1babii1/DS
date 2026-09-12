using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AuthService.Infrastructure.Postgres;

// See DirectoryServiceDbContextFactory for why this exists: without it, `dotnet ef migrations
// bundle` reflects into Program.cs to build the DbContext and that fails to resolve Serilog.
public class AuthDbContextFactory : IDesignTimeDbContextFactory<AuthDbContext>
{
    public AuthDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AuthDbContext>();
        optionsBuilder.UseNpgsql("Server=localhost;Port=5432;Database=platform;User Id=postgres;Password=postgres");

        return new AuthDbContext(optionsBuilder.Options);
    }
}
