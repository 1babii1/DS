using Microsoft.Extensions.Logging;
using DirectoryService.Grpc;
using EmployeeService.Application.Directory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace EmployeeService.Infrastructure.DirectoryGrpc;

public static class DependencyInjectionExtensions
{
    public static IServiceCollection AddDirectoryGrpcClient(
        this IServiceCollection services,
        IConfiguration configuration,
        DirectoryGrpcResilienceSettings? resilienceSettings = null)
    {
        var address = configuration["Directory:GrpcAddress"]
            ?? throw new InvalidOperationException("Configuration 'Directory:GrpcAddress' is not set.");

        services.AddHttpContextAccessor();
        services.AddTransient<TokenForwardingHandler>();

        var mutualTls = Shared.Security.MutualTlsOptions.From(configuration);

        var grpc = services
            .AddGrpcClient<DirectoryLookup.DirectoryLookupClient>(options =>
            {
                options.Address = new Uri(address);
            })
            .AddHttpMessageHandler<TokenForwardingHandler>();

        // With mutual TLS on (ADR 0044) this service presents its own certificate to DirectoryService and accepts DirectoryService
        // only if its certificate was signed by the platform's authority. The address must then be https and name the host in the certificate.
        if (mutualTls.Enabled)
        {
            grpc.ConfigurePrimaryHttpMessageHandler(() => Shared.Security.MutualTls.CreateClientHandler(mutualTls));
        }

        // Retry, circuit breaker and deadlines on a call that crosses a network boundary, applied to the call and not to the HTTP requests under
        // it (ADR 0053). The pipeline holds the breaker's state, so it is one instance for the process.
        var resilience = resilienceSettings ?? new DirectoryGrpcResilienceSettings();
        services.AddSingleton(resilience);
        services.AddSingleton(provider => new DirectoryGrpcResilienceInterceptor(
            DirectoryGrpcResilience.CreatePipeline(resilience, provider.GetService<ILoggerFactory>()?.CreateLogger("DirectoryGrpcResilience")), resilience));
        grpc.AddInterceptor<DirectoryGrpcResilienceInterceptor>();

        services.AddScoped<IDirectoryLookupClient, DirectoryLookupClient>();

        return services;
    }
}