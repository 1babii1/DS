using System.Diagnostics;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Shared.Redis;
using Xunit.Abstractions;

namespace DirectoryService.IntegrationTests;

// The cache is an accelerator, not a source of truth (ADR 0036): with Redis gone the answer must still come from the source, and
// once the breaker has noticed, without waiting for Redis at all. Redis here is a port nothing listens on, wired the way
// Program.cs wires it. Measured before the fix: every read took about 6 s.
public class RedisDownTests(ITestOutputHelper output)
{
    private static ServiceProvider Wire(bool breaker)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddStackExchangeRedisCache(setup => setup.Configuration = RedisConnectionString.Resilient("127.0.0.1:1,password=x"));
        if (breaker)
        {
            services.AddCircuitBreakerToDistributedCache();
        }

        services.AddHybridCache(options => options.DefaultEntryOptions = new HybridCacheEntryOptions
        {
            LocalCacheExpiration = TimeSpan.FromMinutes(5),
            Expiration = TimeSpan.FromMinutes(30),
        });
        return services.BuildServiceProvider();
    }

    private static async Task<long> TimedRead(HybridCache cache, string key, string expected)
    {
        var watch = Stopwatch.StartNew();
        var value = await cache.GetOrCreateAsync(key, _ => ValueTask.FromResult(expected));
        Assert.Equal(expected, value);
        return watch.ElapsedMilliseconds;
    }

    [Fact]
    public async Task With_redis_unreachable_reads_still_answer_and_after_a_few_failures_cost_nothing()
    {
        await using var provider = Wire(breaker: true);
        var cache = provider.GetRequiredService<HybridCache>();

        // The first few calls find out that Redis is gone; each is bounded by the short timeouts.
        for (var i = 0; i < 6; i++)
        {
            var slow = await TimedRead(cache, $"warm-{i}", "from the database");
            output.WriteLine($"while finding out, read {i}: {slow} ms");
            Assert.InRange(slow, 0, 3000);
        }

        var after = new List<long>();
        for (var i = 0; i < 20; i++)
        {
            after.Add(await TimedRead(cache, $"after-{i}", "from the database"));
        }

        output.WriteLine($"after the breaker opened: max {after.Max()} ms");
        Assert.True(after.Max() < 100, $"reads still waited for Redis: {string.Join(',', after)} ms");
    }

    [Fact]
    public async Task Without_the_breaker_every_read_still_pays_for_the_dead_redis()
    {
        await using var provider = Wire(breaker: false);
        var cache = provider.GetRequiredService<HybridCache>();

        await TimedRead(cache, "w", "x");
        var later = await TimedRead(cache, "later", "x");

        output.WriteLine($"without the breaker, a later read: {later} ms");
        Assert.True(later >= 100, "this documents the cost the breaker removes; if it fails, Redis got faster to fail and the breaker may be unnecessary");
    }
}
