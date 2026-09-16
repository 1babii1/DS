using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SearchService.Infrastructure.Postgres;

public static class DependencyInjectionExtensions
{
    public static IServiceCollection AddSearchPostgresInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("SearchServiceDb")
            ?? throw new InvalidOperationException("Connection string 'SearchServiceDb' is not configured.");

        services.AddDbContext<SearchDbContext>(options => options.UseNpgsql(connectionString));

        return services;
    }
}
