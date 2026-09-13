using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Shared.Cors;

public static class CorsExtensions
{
    public static IServiceCollection AddFrameworkCors(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddCors();

        return services.Configure<CorsSettings>(configuration.GetSection(CorsSettings.CORS));
    }

    public static void ConfigureCors(this WebApplication app)
    {
        CorsSettings corsSettings = app.Services.GetRequiredService<IOptions<CorsSettings>>().Value;

        app.UseCors(config =>
        {
            if (corsSettings.AllowedOrigins.Count > 0)
                config.WithOrigins(corsSettings.AllowedOrigins.ToArray());

            if (corsSettings.AllowCredentials)
                config.AllowCredentials();

            if (corsSettings.AllowedHeaders.Count > 0 && !corsSettings.AllowedHeaders.Contains("*"))
            {
                config.WithHeaders(corsSettings.AllowedHeaders.ToArray());
            }
            else
            {
                config.AllowAnyHeader();
            }

            if (corsSettings.AllowedMethods.Count > 0 && !corsSettings.AllowedMethods.Contains("*"))
            {
                config.WithMethods(corsSettings.AllowedMethods.ToArray());
            }
            else
            {
                config.AllowAnyMethod();
            }
        });
    }
}