using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Shared.Resilience;

/// <summary>A kind of request that gets a limit of its own, so that a flood of it cannot use up the places meant for everything else.</summary>
public sealed class LoadClassOptions
{
    /// <summary>The paths that belong to the class (each matched as a path prefix: <c>/api/search</c> covers <c>/api/search/x</c>).</summary>
    public string[] PathPrefixes { get; set; } = [];

    public int MaxConcurrentRequests { get; set; } = 32;

    public int QueueLimit { get; set; } = 16;
}

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

    /// <summary>
    /// Bulkheads (ADR 0045): classes of request with their own limit, inside the overall one. A request of a class needs a place in
    /// its class <b>and</b> a place overall. Configured as <c>LoadShedding:Classes:&lt;name&gt;:PathPrefixes:0</c> and so on.
    /// </summary>
    public Dictionary<string, LoadClassOptions> Classes { get; set; } = [];
}

/// <summary>
/// Keeps an overloaded service answering the requests it accepts quickly, instead of answering all of them slowly (ADR 0038).
/// With no limit, a burst past capacity piles up in the server's queue and every request waits behind all the others until
/// callers time out; the work is done and nobody can use it. With a limit, a bounded number run, a bounded number wait briefly,
/// and the rest are refused at once with 503 and a Retry-After, which a client can act on.
/// The queue serves the newest request first: when the queue is full it is the oldest waiter (the one whose caller is closest to
/// giving up anyway) that is turned away.
/// A class (ADR 0045) is a bulkhead: expensive or unreliable work gets a small limit of its own, so that when it backs up it fills
/// its own compartment and not the whole ship.
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
    public const string Overall = "overall";

    private sealed record Compartment(string Name, string[] Prefixes, ConcurrencyLimiter Limiter);

    private readonly ConcurrencyLimiter _overall;
    private readonly List<Compartment> _classes = [];
    private readonly ConcurrentDictionary<string, long> _shed = new();

    public LoadShedder(IOptions<LoadSheddingOptions> options)
    {
        Options = options.Value;
        _overall = NewLimiter(Options.MaxConcurrentRequests, Options.QueueLimit);
        foreach (var (name, settings) in Options.Classes)
        {
            _classes.Add(new Compartment(name, settings.PathPrefixes, NewLimiter(settings.MaxConcurrentRequests, settings.QueueLimit)));
        }
    }

    public LoadSheddingOptions Options { get; }

    /// <summary>How many requests were turned away, by the class that turned them away (or "overall").</summary>
    public IReadOnlyDictionary<string, long> ShedBy => _shed;

    public long ShedCount => _shed.Values.Sum();

    /// <summary>
    /// A place for a request to this path: one in its class if it belongs to one, then one overall, or null (with <paramref name="refusedBy"/>
    /// naming who said no) when it waited as long as allowed. Dispose the result to give the places back.
    /// </summary>
    public async ValueTask<IDisposable?> TryEnterAsync(PathString path, CancellationToken cancellationToken)
    {
        var compartment = _classes.FirstOrDefault(c => c.Prefixes.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase)));

        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(Options.MaxQueueWait);

        RateLimitLease? classLease = null;
        if (compartment is not null)
        {
            classLease = await TryAcquireAsync(compartment.Limiter, compartment.Name, wait.Token, cancellationToken);
            if (classLease is null)
            {
                return null;
            }
        }

        var overallLease = await TryAcquireAsync(_overall, Overall, wait.Token, cancellationToken);
        if (overallLease is null)
        {
            classLease?.Dispose();
            return null;
        }

        return new Places(classLease, overallLease);
    }

    public void Dispose()
    {
        _overall.Dispose();
        foreach (var compartment in _classes)
        {
            compartment.Limiter.Dispose();
        }
    }

    private static ConcurrencyLimiter NewLimiter(int permits, int queue) => new(new ConcurrencyLimiterOptions
    {
        PermitLimit = permits,
        QueueLimit = queue,
        QueueProcessingOrder = QueueProcessingOrder.NewestFirst,
    });

    private async ValueTask<RateLimitLease?> TryAcquireAsync(
        ConcurrencyLimiter limiter, string name, CancellationToken waitToken, CancellationToken requestToken)
    {
        try
        {
            var lease = await limiter.AcquireAsync(1, waitToken);
            if (lease.IsAcquired)
            {
                return lease;
            }
        }
        catch (OperationCanceledException) when (!requestToken.IsCancellationRequested)
        {
            // Waited as long as it is allowed to.
        }

        _shed.AddOrUpdate(name, 1, (_, count) => count + 1);
        return null;
    }

    private sealed class Places(RateLimitLease? inClass, RateLimitLease overall) : IDisposable
    {
        public void Dispose()
        {
            overall.Dispose();
            inClass?.Dispose();
        }
    }
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

        var places = await shedder.TryEnterAsync(path, context.RequestAborted);
        if (places is null)
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

        using (places)
        {
            await next(context);
        }
    }
}
