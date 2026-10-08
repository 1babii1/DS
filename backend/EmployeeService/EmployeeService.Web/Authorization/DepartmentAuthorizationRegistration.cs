using EmployeeService.Application.Authorization;
using EmployeeService.Web.Consumers;
using Shared.Kafka;
using Shared.Outbox;

namespace EmployeeService.Web.Authorization;

public static class DepartmentAuthorizationRegistration
{
    /// <summary>
    /// <c>DepartmentAuthorization:Mode</c> (ADR 0057): <c>Roles</c>, the default, adds no condition; <c>Tree</c> asks OpenFGA and also runs the consumer
    /// that keeps its picture of the departments in step with DirectoryService. Nothing of OpenFGA is registered in roles mode, apart from the
    /// store client the managers endpoint needs to answer "not in use".
    /// </summary>
    public static IServiceCollection AddDepartmentAuthorization(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(DepartmentAuthorizationOptions.SectionName);
        services.Configure<DepartmentAuthorizationOptions>(section);
        var options = section.Get<DepartmentAuthorizationOptions>() ?? new DepartmentAuthorizationOptions();

        // One cache of the store's id for the process: the client itself is made anew for every use.
        services.AddSingleton<OpenFgaStoreState>();
        services.AddHttpClient<IFgaClient, OpenFgaHttpClient>((provider, client) =>
        {
            var configured = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<DepartmentAuthorizationOptions>>().Value;
            client.BaseAddress = new Uri(configured.OpenFgaUrl);
            client.Timeout = configured.Timeout;
        });

        if (options.Mode != DepartmentAuthorizationMode.Tree)
        {
            services.AddScoped<IDepartmentAuthorization, RolesDepartmentAuthorization>();
            return services;
        }

        services.AddScoped<IDepartmentAuthorization, TreeDepartmentAuthorization>();
        services.AddScoped<DepartmentTreeSync>();

        services.Configure<DirectoryConsumerOptions>(consumer =>
        {
            consumer.BootstrapServers = configuration["Kafka:BootstrapServers"]
                ?? throw new InvalidOperationException("Configuration 'Kafka:BootstrapServers' is not set.");
            consumer.Security = KafkaSecurityOptions.FromConfiguration(configuration);
        });
        services.AddHostedService<DirectoryEventsConsumer>();

        return services;
    }
}
