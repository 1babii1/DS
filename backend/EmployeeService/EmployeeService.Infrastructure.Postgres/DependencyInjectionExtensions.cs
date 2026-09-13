using EmployeeService.Application.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EmployeeService.Infrastructure.Postgres;

public static class DependencyInjectionExtensions
{
    public static IServiceCollection AddInfrastructurePostgres(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("EmployeeServiceDb")
            ?? throw new InvalidOperationException("Connection string 'EmployeeServiceDb' is not configured.");

        services.AddDbContext<EmployeeDbContext>(options => options.UseNpgsql(connectionString));
        services.AddScoped<IReadDbContext>(sp => sp.GetRequiredService<EmployeeDbContext>());
        services.AddScoped<IEmployeeRepository, EmployeeRepository>();

        return services;
    }
}