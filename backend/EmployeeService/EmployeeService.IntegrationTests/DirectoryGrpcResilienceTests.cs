using System.Diagnostics;
using EmployeeService.Infrastructure.DirectoryGrpc;
using Polly;
using Polly.CircuitBreaker;

namespace EmployeeService.IntegrationTests;

// The policy on the gRPC call to DirectoryService (ADR 0051, 0052), exercised as a pipeline with the same strategies the service builds, with
// shorter times. What these pin was measured with Toxiproxy: a hire waited 21 s with no deadline of its own, and an open breaker still made a
// call sleep through its retry pauses (1.4 s) before failing.
public class DirectoryGrpcResilienceTests
{
    [Fact]
    public async Task A_call_the_open_breaker_refuses_fails_at_once_and_is_not_retried()
    {
        var settings = new DirectoryGrpcResilienceSettings
        {
            RetryDelay = TimeSpan.FromMilliseconds(300),
            BreakDuration = TimeSpan.FromSeconds(30),
            AttemptTimeout = TimeSpan.FromSeconds(2),
            TotalTimeout = TimeSpan.FromSeconds(20),
        };
        var pipeline = Build(settings);
        var attempts = 0;
        ValueTask<HttpResponseMessage> Fail(ResilienceContext _)
        {
            Interlocked.Increment(ref attempts);
            throw new HttpRequestException("connection reset");
        }

        // Enough failed attempts to open it (five are needed, and one call makes up to four).
        for (var i = 0; i < 3; i++)
        {
            await Assert.ThrowsAnyAsync<Exception>(() => pipeline.ExecuteAsync(Fail, ResilienceContextPool.Shared.Get()).AsTask());
        }

        var before = Volatile.Read(ref attempts);
        var clock = Stopwatch.StartNew();
        await Assert.ThrowsAsync<BrokenCircuitException>(() => pipeline.ExecuteAsync(Fail, ResilienceContextPool.Shared.Get()).AsTask());

        Assert.Equal(before, Volatile.Read(ref attempts));
        Assert.True(clock.ElapsedMilliseconds < 150, $"the open breaker took {clock.ElapsedMilliseconds} ms to refuse a call");
    }

    [Fact]
    public async Task A_call_that_never_answers_stops_at_the_deadline_of_the_whole_call_not_after_every_attempt()
    {
        var settings = new DirectoryGrpcResilienceSettings
        {
            AttemptTimeout = TimeSpan.FromMilliseconds(400),
            RetryDelay = TimeSpan.FromMilliseconds(50),
            TotalTimeout = TimeSpan.FromMilliseconds(900),
            MinimumThroughput = 1000,
        };
        var pipeline = Build(settings);

        var clock = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<Exception>(() => pipeline.ExecuteAsync(
            async context =>
            {
                await Task.Delay(Timeout.Infinite, context.CancellationToken);
                return new HttpResponseMessage();
            },
            ResilienceContextPool.Shared.Get()).AsTask());

        Assert.True(clock.ElapsedMilliseconds < 1300, $"the call took {clock.ElapsedMilliseconds} ms; four attempts of 400 ms would be about 1800");
    }

    [Fact]
    public async Task A_transient_failure_is_still_retried_and_a_slow_answer_inside_the_limits_still_succeeds()
    {
        var settings = new DirectoryGrpcResilienceSettings { RetryDelay = TimeSpan.FromMilliseconds(20), MinimumThroughput = 1000 };
        var pipeline = Build(settings);
        var attempts = 0;

        var response = await pipeline.ExecuteAsync(
            async context =>
            {
                if (Interlocked.Increment(ref attempts) < 3)
                {
                    throw new HttpRequestException("reset");
                }

                await Task.Delay(100, context.CancellationToken);
                return new HttpResponseMessage();
            },
            ResilienceContextPool.Shared.Get());

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal(3, attempts);
    }

    private static ResiliencePipeline<HttpResponseMessage> Build(DirectoryGrpcResilienceSettings settings)
    {
        var builder = new ResiliencePipelineBuilder<HttpResponseMessage>();
        DirectoryGrpcResilience.Configure(builder, settings);
        return builder.Build();
    }
}
