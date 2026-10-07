using System.Diagnostics;
using EmployeeService.Application.Directory;
using EmployeeService.Infrastructure.DirectoryGrpc;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EmployeeService.IntegrationTests;

// The policy on the gRPC call to DirectoryService (ADR 0051, 0052, 0053), applied to the call itself. The failures it exists for were measured
// with Toxiproxy: a hire waited 21 s with no deadline of its own, an open breaker still slept through retry pauses, and a refused connection
// never reached the retry or the breaker at all because it fails inside the gRPC client before any HTTP handler runs.
public class DirectoryGrpcResilienceTests
{
    private static readonly Method<string, string> Echo =
        new(MethodType.Unary, "test", "echo", Marshallers.StringMarshaller, Marshallers.StringMarshaller);

    [Fact]
    public async Task A_call_the_open_breaker_refuses_fails_at_once_and_is_not_retried()
    {
        var settings = new DirectoryGrpcResilienceSettings { RetryDelay = TimeSpan.FromMilliseconds(300), BreakDuration = TimeSpan.FromSeconds(30) };
        var attempts = 0;
        var sut = Interceptor(settings, (_, _) => { Interlocked.Increment(ref attempts); return Failing(StatusCode.Unavailable); });

        // Enough failed attempts to open it: five are needed, and one call makes up to four.
        for (var i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<RpcException>(() => Call(sut));
        }

        var before = Volatile.Read(ref attempts);
        var clock = Stopwatch.StartNew();
        var refused = await Assert.ThrowsAsync<RpcException>(() => Call(sut));

        Assert.Equal(StatusCode.Unavailable, refused.StatusCode);
        Assert.Contains("circuit is open", refused.Status.Detail);
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
        var sut = Interceptor(settings, (_, context) => Silent(context.Options));

        var clock = Stopwatch.StartNew();
        var failure = await Assert.ThrowsAsync<RpcException>(() => Call(sut));

        Assert.Equal(StatusCode.DeadlineExceeded, failure.StatusCode);
        Assert.True(clock.ElapsedMilliseconds < 1300, $"the call took {clock.ElapsedMilliseconds} ms; four attempts of 400 ms would be about 1800");
    }

    [Fact]
    public async Task Callers_waiting_on_a_silent_dependency_open_the_breaker_so_that_later_attempts_are_refused_at_once()
    {
        // The shape of the 6 s step in the drill: several callers at once, each attempt ending at its own deadline.
        var settings = new DirectoryGrpcResilienceSettings
        {
            AttemptTimeout = TimeSpan.FromMilliseconds(300),
            RetryDelay = TimeSpan.FromMilliseconds(50),
            TotalTimeout = TimeSpan.FromSeconds(5),
            BreakDuration = TimeSpan.FromSeconds(30),
        };
        var attempts = 0;
        var sut = Interceptor(settings, (_, context) => { Interlocked.Increment(ref attempts); return Silent(context.Options); });

        var callers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            var clock = Stopwatch.StartNew();
            await Assert.ThrowsAsync<RpcException>(() => Call(sut));
            return clock.ElapsedMilliseconds;
        })).ToArray();
        var durations = await Task.WhenAll(callers);

        // Four callers x four attempts would be sixteen attempts of 300 ms each; the breaker opens after five failures, so the rest are refused.
        Assert.True(attempts < 16, $"{attempts} attempts reached the dependency; the breaker never opened");
        Assert.True(durations.Max() < 1500, $"the slowest caller waited {durations.Max()} ms");
    }

    [Fact]
    public async Task A_transient_failure_is_retried_and_the_answer_that_follows_is_returned()
    {
        var attempts = 0;
        var sut = Interceptor(
            new DirectoryGrpcResilienceSettings { RetryDelay = TimeSpan.FromMilliseconds(20), MinimumThroughput = 1000 },
            (_, _) => Interlocked.Increment(ref attempts) < 3 ? Failing(StatusCode.Unavailable) : Answering("found"));

        Assert.Equal("found", await Call(sut));
        Assert.Equal(3, attempts);
    }

    [Theory]
    [InlineData(StatusCode.PermissionDenied)]
    [InlineData(StatusCode.Unauthenticated)]
    [InlineData(StatusCode.NotFound)]
    public async Task An_answer_that_is_a_refusal_is_not_retried_and_does_not_count_against_the_breaker(StatusCode code)
    {
        var attempts = 0;
        var sut = Interceptor(
            new DirectoryGrpcResilienceSettings { RetryDelay = TimeSpan.FromMilliseconds(20) },
            (_, _) => { Interlocked.Increment(ref attempts); return Failing(code); });

        for (var i = 0; i < 10; i++)
        {
            var failure = await Assert.ThrowsAsync<RpcException>(() => Call(sut));
            Assert.Equal(code, failure.StatusCode);
        }

        Assert.Equal(10, attempts);
    }

    [Fact]
    public async Task A_refused_connection_is_retried_and_opens_the_breaker_through_the_real_client()
    {
        // Nothing listens on this port: the failure the gRPC client raises inside its own connection manager, before an HTTP handler runs.
        // This is the call that the HTTP-level retry and breaker never saw (ADR 0051).
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Directory:GrpcAddress"] = "http://127.0.0.1:1" }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDirectoryGrpcClient(configuration, new DirectoryGrpcResilienceSettings { RetryDelay = TimeSpan.FromMilliseconds(40), BreakDuration = TimeSpan.FromSeconds(30) });
        await using var provider = services.BuildServiceProvider();

        // Each hire is its own scope, as in the service; the breaker has to be the one of the process, not of a scope.
        async Task<(Exception? Failure, long Ms)> Lookup()
        {
            await using var scope = provider.CreateAsyncScope();
            var clock = Stopwatch.StartNew();
            try
            {
                await scope.ServiceProvider.GetRequiredService<IDirectoryLookupClient>().GetDepartmentAsync(Guid.NewGuid(), CancellationToken.None);
                return (null, clock.ElapsedMilliseconds);
            }
            catch (Exception exception)
            {
                return (exception, clock.ElapsedMilliseconds);
            }
        }

        var first = await Lookup();
        Assert.NotNull(first.Failure);
        Assert.True(first.Ms >= 40 + 80 + 160 - 30, $"the first call took {first.Ms} ms: it was not retried with pauses");

        await Lookup();
        var refused = await Lookup();

        Assert.NotNull(refused.Failure);
        Assert.True(refused.Ms < 100, $"a call after the breaker opened took {refused.Ms} ms");
    }

    private static Harness Interceptor(
        DirectoryGrpcResilienceSettings settings,
        Func<string, ClientInterceptorContext<string, string>, AsyncUnaryCall<string>> next) =>
        new(new DirectoryGrpcResilienceInterceptor(DirectoryGrpcResilience.CreatePipeline(settings), settings), next);

    private static Task<string> Call(Harness harness) => harness.CallAsync();

    private sealed class Harness(
        DirectoryGrpcResilienceInterceptor interceptor,
        Func<string, ClientInterceptorContext<string, string>, AsyncUnaryCall<string>> next)
    {
        public async Task<string> CallAsync() =>
            await interceptor.AsyncUnaryCall(
                "hello",
                new ClientInterceptorContext<string, string>(Echo, null, new CallOptions()),
                (request, context) => next(request, context)).ResponseAsync;
    }

    private static AsyncUnaryCall<string> Failing(StatusCode code) =>
        Wrap(Task.FromException<string>(new RpcException(new Status(code, "test failure"))));

    private static AsyncUnaryCall<string> Answering(string value) => Wrap(Task.FromResult(value));

    private static AsyncUnaryCall<string> Silent(CallOptions options) => Wrap(Task.Run<string>(async () =>
    {
        try
        {
            var wait = options.Deadline.HasValue ? options.Deadline.Value - DateTime.UtcNow : Timeout.InfiniteTimeSpan;
            await Task.Delay(wait, options.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw new RpcException(new Status(StatusCode.Cancelled, "cancelled"));
        }

        throw new RpcException(new Status(StatusCode.DeadlineExceeded, "deadline"));
    }));

    private static AsyncUnaryCall<string> Wrap(Task<string> response) =>
        new(response, Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });
}
