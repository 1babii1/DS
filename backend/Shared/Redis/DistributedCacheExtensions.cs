using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Shared.Redis;

public static class DistributedCacheExtensions
{
    /// <summary>Wraps whatever <see cref="IDistributedCache"/> is registered so far in a circuit breaker (ADR 0036). Call it after registering the cache.</summary>
    public static IServiceCollection AddCircuitBreakerToDistributedCache(this IServiceCollection services)
    {
        var descriptor = services.LastOrDefault(d => d.ServiceType == typeof(IDistributedCache))
            ?? throw new InvalidOperationException("Register a distributed cache before adding the circuit breaker to it.");
        services.Remove(descriptor);

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IDistributedCache>(provider =>
        {
            var inner = descriptor.ImplementationInstance as IDistributedCache
                ?? (descriptor.ImplementationFactory is { } factory
                    ? (IDistributedCache)factory(provider)
                    : (IDistributedCache)ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!));
            return new CircuitBreakingDistributedCache(
                inner, provider.GetRequiredService<ILogger<CircuitBreakingDistributedCache>>(), provider.GetRequiredService<TimeProvider>());
        });
        return services;
    }
}
