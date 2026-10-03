using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace Shared.Redis;

/// <summary>
/// Puts a circuit breaker in front of a distributed cache (ADR 0036). The cache is an accelerator: when it fails, the answer
/// is a miss and the caller goes to the source of truth. Without a breaker, a dead cache still costs every request the time its
/// call takes to fail (measured: about half a second per operation even with short timeouts); with one, after a few failures in
/// a row the cache is skipped outright for a while and costs nothing, then one call is let through to see whether it is back.
///
/// What is given up: while the breaker is open, writes and invalidations to the cache are dropped, so an entry cached before the
/// outage can outlive the change that should have evicted it, until its own expiry. That is the trade the best-effort cache
/// already made; the breaker only stops paying for it on every request.
/// </summary>
public sealed class CircuitBreakingDistributedCache(
    IDistributedCache inner,
    ILogger<CircuitBreakingDistributedCache> logger,
    TimeProvider clock,
    int failuresToOpen = 3,
    TimeSpan? openFor = null) : IDistributedCache
{
    private readonly TimeSpan _openFor = openFor ?? TimeSpan.FromSeconds(15);
    private readonly object _gate = new();
    private int _consecutiveFailures;
    private DateTimeOffset _openUntil = DateTimeOffset.MinValue;
    private bool _trialInFlight;

    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                return clock.GetUtcNow() < _openUntil;
            }
        }
    }

    public byte[]? Get(string key) => Run(() => inner.Get(key), null);

    public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => RunAsync(() => inner.GetAsync(key, token), null);

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options) =>
        Run<object?>(() => { inner.Set(key, value, options); return null; }, null);

    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) =>
        RunAsync<object?>(async () => { await inner.SetAsync(key, value, options, token); return null; }, null);

    public void Refresh(string key) => Run<object?>(() => { inner.Refresh(key); return null; }, null);

    public Task RefreshAsync(string key, CancellationToken token = default) =>
        RunAsync<object?>(async () => { await inner.RefreshAsync(key, token); return null; }, null);

    public void Remove(string key) => Run<object?>(() => { inner.Remove(key); return null; }, null);

    public Task RemoveAsync(string key, CancellationToken token = default) =>
        RunAsync<object?>(async () => { await inner.RemoveAsync(key, token); return null; }, null);

    private T Run<T>(Func<T> call, T fallback)
    {
        if (!TryEnter())
        {
            return fallback;
        }

        try
        {
            var result = call();
            Succeeded();
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Failed(ex);
            return fallback;
        }
    }

    private async Task<T> RunAsync<T>(Func<Task<T>> call, T fallback)
    {
        if (!TryEnter())
        {
            return fallback;
        }

        try
        {
            var result = await call();
            Succeeded();
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Failed(ex);
            return fallback;
        }
    }

    // Closed: everything goes through. Open: nothing does. Once the open period has passed, exactly one call goes through to
    // find out (half-open); the rest keep skipping until that one reports.
    private bool TryEnter()
    {
        lock (_gate)
        {
            if (_consecutiveFailures < failuresToOpen)
            {
                return true;
            }

            if (clock.GetUtcNow() < _openUntil || _trialInFlight)
            {
                return false;
            }

            _trialInFlight = true;
            return true;
        }
    }

    private void Succeeded()
    {
        bool wasOpen;
        lock (_gate)
        {
            wasOpen = _consecutiveFailures >= failuresToOpen;
            _consecutiveFailures = 0;
            _trialInFlight = false;
            _openUntil = DateTimeOffset.MinValue;
        }

        if (wasOpen)
        {
            logger.LogInformation("Distributed cache is back; using it again");
        }
    }

    private void Failed(Exception ex)
    {
        bool opened;
        lock (_gate)
        {
            _consecutiveFailures++;
            _trialInFlight = false;
            opened = _consecutiveFailures >= failuresToOpen;
            if (opened)
            {
                _openUntil = clock.GetUtcNow() + _openFor;
            }
        }

        if (opened)
        {
            logger.LogWarning(ex, "Distributed cache failed {Failures} times in a row; skipping it for {OpenFor}", failuresToOpen, _openFor);
        }
        else
        {
            logger.LogDebug(ex, "Distributed cache call failed; treated as a miss");
        }
    }
}
