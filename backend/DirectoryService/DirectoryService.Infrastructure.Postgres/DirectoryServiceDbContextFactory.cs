using Microsoft.EntityFrameworkCore.Design;

namespace DirectoryService.Infrastructure.Postgres;

// EF tooling (migrations bundle) otherwise reflects into Program.cs/the Host to construct
// the DbContext, which fails to resolve third-party assemblies like Serilog in that isolated
// context - and is ambiguous here anyway since this DbContext has two constructors. This
// factory sidesteps both problems; the connection string is a placeholder EF replaces at
// bundle-run time via `efbundle --connection ...`.
public class DirectoryServiceDbContextFactory : IDesignTimeDbContextFactory<DirectoryServiceDbContext>
{
    public DirectoryServiceDbContext CreateDbContext(string[] args) =>
        new("Server=localhost;Port=5432;Database=platform;User Id=postgres;Password=postgres");
}
