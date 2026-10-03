using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Shared.Resilience;

public sealed class LoadSheddingOptions
{
    public const string SectionName = "LoadShedding";

    /// <summary>How many requests a process works on at once. Past this, requests wait in the queue.</summary>
    public int MaxConcurrentRequests { get; set; } = 256;

    /// <summary>How many requests may wait. Past this, the oldest waiting request is turned away to make room for the newest.</summary>
    public int QueueLimit { get; set; } = 64;

    /// <summary>The longest a request waits for a place before it is turned away.</summary>
    public TimeSpan MaxQueueWait { get; set; } = TimeSpan.FromSeconds(1);

    public int RetryAfterSeconds { get; set; } = 1;

    /// <summary>
    /// Paths that are never shed: the probes (shedding them would make an overloaded instance look dead and get it restarted
    /// at the worst moment) and the long-lived connections, which would hold a place for as long as they last.
    /// </summary>
    public string[] ExemptPrefixes { get; set; } = ["/health", "/metrics", "/hubs", "/mcp"];
}

/// <summary>
/// Keeps an overloaded service answering the requests it accepts quickly, instead of answering all of them slowly (ADR 0038).
/// With no limit, a burst past capacity piles up in the server's queue and every request waits behind all the others until
/// callers time out; the work is done and nobody can use it. With a limit, a bounded number run, a bounded number wait briefly,
/// and the rest are refused at once with 503 and a Retry-After, which a client can act on.
/// The queue serves the newest request first: when the queue is full it is the oldest waiter (the one whose caller is closest to
/// giving up anyway) that is turned away.
/// </summary>
public static class LoadSheddingExtensions
{
    public static IServiceCollection AddLoadShedding(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<LoadSheddingOptions>(configuration.GetSection(LoadSheddingOptions.SectionName));
        services.AddSingleton<LoadShedder>();
        return services;
    }

    public static IApplicationBuilder UseLoadShedding(this IApplicationBuilder app) => app.UseMiddleware<LoadSheddingMiddleware>();
}

public sealed class LoadShedder : IDisposable
{
    private readonly ConcurrencyLimiter _limiter;
    private long _shed;

    public LoadShedder(IOptions<LoadSheddingOptions> options)
    {
        Options = options.Value;
        _limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions
        {
            PermitLimit = Options.MaxConcurrentRequests,
            QueueLimit = Options.QueueLimit,
            QueueProcessingOrder = QueueProcessingOrder.NewestFirst,
        });
    }

    public LoadSheddingOptions Options { get; }

    public async ValueTask<RateLimitLease?> TryEnterAsync(CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(Options.MaxQueueWait);

        try
        {
            var lease = await _limiter.AcquireAsync(1, wait.Token);
            if (lease.IsAcquired)
            {
                return lease;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Waited as long as it is allowed to.
        }

        Interlocked.Increment(ref _shed);
        return null;
    }

    public long ShedCount => Interlocked.Read(ref _shed);

    public void Dispose() => _limiter.Dispose();
}

public sealed class LoadSheddingMiddleware(RequestDelegate next, LoadShedder shedder, ILogger<LoadSheddingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        if (shedder.Options.ExemptPrefixes.Any(prefix => path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            await next(context);
            return;
        }

        var lease = await shedder.TryEnterAsync(context.RequestAborted);
        if (lease is null)
        {
            if (context.RequestAborted.IsCancellationRequested)
            {
                return;
            }

            logger.LogWarning("Shedding {Method} {Path}: over capacity", context.Request.Method, path);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.RetryAfter = shedder.Options.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await context.Response.WriteAsJsonAsync(
                Envelope.Fail(Error.Unavailable("service.overloaded", "The service is over capacity; retry shortly")),
                context.RequestAborted);
            return;
        }

        using (lease)
        {
            await next(context);
        }
    }
}
