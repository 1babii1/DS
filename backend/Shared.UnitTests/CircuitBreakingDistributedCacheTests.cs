using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.Redis;

namespace Shared.UnitTests;

public class CircuitBreakingDistributedCacheTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FlakyCache : IDistributedCache
    {
        private readonly Dictionary<string, byte[]> _data = [];

        public bool Down { get; set; }

        public int Calls { get; private set; }

        public byte[]? Get(string key)
        {
            Enter();
            return _data.GetValueOrDefault(key);
        }

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
        {
            Enter();
            return Task.FromResult(_data.GetValueOrDefault(key));
        }

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        {
            Enter();
            _data[key] = value;
        }

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            Enter();
            _data[key] = value;
            return Task.CompletedTask;
        }

        public void Refresh(string key) => Enter();

        public Task RefreshAsync(string key, CancellationToken token = default)
        {
            Enter();
            return Task.CompletedTask;
        }

        public void Remove(string key)
        {
            Enter();
            _data.Remove(key);
        }

        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            Enter();
            _data.Remove(key);
            return Task.CompletedTask;
        }

        private void Enter()
        {
            Calls++;
            if (Down)
            {
                throw new InvalidOperationException("redis is down");
            }
        }
    }

    private sealed class CancellingCache : IDistributedCache
    {
        public byte[]? Get(string key) => throw new OperationCanceledException();

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => throw new OperationCanceledException();

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw new OperationCanceledException();

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) =>
            throw new OperationCanceledException();

        public void Refresh(string key) => throw new OperationCanceledException();

        public Task RefreshAsync(string key, CancellationToken token = default) => throw new OperationCanceledException();

        public void Remove(string key) => throw new OperationCanceledException();

        public Task RemoveAsync(string key, CancellationToken token = default) => throw new OperationCanceledException();
    }

    private static (CircuitBreakingDistributedCache Cache, FlakyCache Inner, Clock Clock) Build()
    {
        var inner = new FlakyCache();
        var clock = new Clock(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
        var cache = new CircuitBreakingDistributedCache(
            inner, NullLogger<CircuitBreakingDistributedCache>.Instance, clock, 3, TimeSpan.FromSeconds(15));
        return (cache, inner, clock);
    }

    [Fact]
    public async Task A_healthy_cache_is_passed_straight_through()
    {
        var (cache, inner, _) = Build();

        await cache.SetAsync("k", [1, 2], new DistributedCacheEntryOptions());

        Assert.Equal<byte[]?>([1, 2], await cache.GetAsync("k"));
        Assert.False(cache.IsOpen);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task A_failing_call_is_a_miss_not_an_exception()
    {
        var (cache, inner, _) = Build();
        inner.Down = true;

        Assert.Null(await cache.GetAsync("k"));
        await cache.SetAsync("k", [1], new DistributedCacheEntryOptions());
        await cache.RemoveAsync("k");
    }

    [Fact]
    public async Task After_three_failures_in_a_row_the_cache_is_skipped_without_being_called()
    {
        var (cache, inner, _) = Build();
        inner.Down = true;
        for (var i = 0; i < 3; i++)
        {
            await cache.GetAsync("k");
        }

        Assert.True(cache.IsOpen);
        var callsWhenOpened = inner.Calls;

        for (var i = 0; i < 100; i++)
        {
            Assert.Null(await cache.GetAsync("k"));
            await cache.SetAsync("k", [1], new DistributedCacheEntryOptions());
        }

        Assert.Equal(callsWhenOpened, inner.Calls);
    }

    [Fact]
    public async Task A_success_in_between_resets_the_count()
    {
        var (cache, inner, _) = Build();
        inner.Down = true;
        await cache.GetAsync("k");
        await cache.GetAsync("k");
        inner.Down = false;
        await cache.GetAsync("k");
        inner.Down = true;
        await cache.GetAsync("k");
        await cache.GetAsync("k");

        Assert.False(cache.IsOpen);
    }

    [Fact]
    public async Task After_the_open_period_one_call_tries_and_if_it_works_the_cache_is_used_again()
    {
        var (cache, inner, clock) = Build();
        inner.Down = true;
        for (var i = 0; i < 3; i++)
        {
            await cache.GetAsync("k");
        }

        clock.Now += TimeSpan.FromSeconds(16);
        inner.Down = false;
        await cache.SetAsync("k", [9], new DistributedCacheEntryOptions());

        Assert.False(cache.IsOpen);
        Assert.Equal<byte[]?>([9], await cache.GetAsync("k"));
    }

    [Fact]
    public async Task After_the_open_period_a_failing_trial_closes_the_door_again_for_another_period()
    {
        var (cache, inner, clock) = Build();
        inner.Down = true;
        for (var i = 0; i < 3; i++)
        {
            await cache.GetAsync("k");
        }

        clock.Now += TimeSpan.FromSeconds(16);
        var before = inner.Calls;
        await cache.GetAsync("k");
        await cache.GetAsync("k");
        await cache.GetAsync("k");

        Assert.Equal(before + 1, inner.Calls);
        Assert.True(cache.IsOpen);
    }

    [Fact]
    public async Task Cancellation_is_not_a_cache_failure()
    {
        var cache = new CircuitBreakingDistributedCache(
            new CancellingCache(), NullLogger<CircuitBreakingDistributedCache>.Instance, TimeProvider.System, 1);

        await Assert.ThrowsAsync<OperationCanceledException>(() => cache.GetAsync("k"));
        Assert.False(cache.IsOpen);
    }
}
