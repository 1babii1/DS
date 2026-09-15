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
        IConfiguration configuration)
    {
        var address = configuration["Directory:GrpcAddress"]
            ?? throw new InvalidOperationException("Configuration 'Directory:GrpcAddress' is not set.");

        services.AddHttpContextAccessor();
        services.AddTransient<TokenForwardingHandler>();

        services
            .AddGrpcClient<DirectoryLookup.DirectoryLookupClient>(options =>
            {
                options.Address = new Uri(address);
            })
            .AddHttpMessageHandler<TokenForwardingHandler>()

            // Retries and circuit-breaking on a call that crosses a network boundary:
            // DirectoryService being briefly unavailable shouldn't fail every hire attempt outright.
            .AddResilienceHandler("directory-grpc", builder =>
            {
                builder.AddRetry(new()
                {
                    MaxRetryAttempts = 3,
                    Delay = TimeSpan.FromMilliseconds(200),
                    BackoffType = Polly.DelayBackoffType.Exponential,
                });

                builder.AddCircuitBreaker(new()
                {
                    FailureRatio = 0.5,
                    SamplingDuration = TimeSpan.FromSeconds(30),
                    MinimumThroughput = 5,
                    BreakDuration = TimeSpan.FromSeconds(15),
                });

                builder.AddTimeout(TimeSpan.FromSeconds(5));
            });

        services.AddScoped<IDirectoryLookupClient, DirectoryLookupClient>();

        return services;
    }
}