using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace NotificationService.Infrastructure.Postgres;

public static class DependencyInjectionExtensions
{
    public static IServiceCollection AddNotificationInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("NotificationServiceDb")
            ?? throw new InvalidOperationException("Connection string 'NotificationServiceDb' is not configured.");

        services.AddDbContext<NotificationDbContext>(options => options.UseNpgsql(connectionString));

        return services;
    }
}
