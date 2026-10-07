using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace EmployeeService.Infrastructure.DirectoryGrpc;

/// <summary>The numbers of the policy on the gRPC call to DirectoryService; the defaults are what runs, tests pass smaller ones.</summary>
public sealed record DirectoryGrpcResilienceSettings
{
    public int MaxRetryAttempts { get; init; } = 3;

    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(200);

    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(3);

    public TimeSpan TotalTimeout { get; init; } = TimeSpan.FromSeconds(8);

    public int MinimumThroughput { get; init; } = 5;

    public TimeSpan BreakDuration { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How far back the breaker looks. Failures are a ratio of the calls in this window, so a long window full of healthy calls
    /// hides a dependency that has just gone silent: with 30 s, a dozen failures after a busy half minute never reached half (ADR 0053).</summary>
    public TimeSpan SamplingDuration { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// The policy for the call to DirectoryService, applied to the call itself and not to the HTTP requests underneath it. A call that is refused
/// because nothing listens fails inside the gRPC client's connection manager, before any HTTP handler runs, so a retry and a breaker placed as
/// HTTP handlers never saw the commonest failure there is (ADR 0051 item 4, ADR 0053).
/// Order: total deadline, retry, circuit breaker; the deadline of one attempt is the gRPC deadline of the call.
/// </summary>
public static class DirectoryGrpcResilience
{
    /// <summary>What counts as the dependency being unwell: it cannot be reached, or it did not answer in time. A "not found" or a refusal
    /// of the credentials is an answer, and neither retried nor counted by the breaker.</summary>
    public static bool IsTransient(RpcException exception) =>
        exception.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded;

    public static ResiliencePipeline CreatePipeline(DirectoryGrpcResilienceSettings? settings = null, ILogger? logger = null)
    {
        settings ??= new DirectoryGrpcResilienceSettings();

        // One line per event in the form Polly's own telemetry writes, so that what the breaker did is visible in the log (and to scripts/toxiproxy-drill.sh).
        ValueTask Event(string name)
        {
            logger?.LogWarning("Resilience event occurred. EventName: '{EventName}', Source: 'DirectoryLookupClient-directory-grpc'", name);
            return default;
        }

        return new ResiliencePipelineBuilder()
            .AddTimeout(settings.TotalTimeout)
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = settings.MaxRetryAttempts,
                Delay = settings.RetryDelay,
                BackoffType = DelayBackoffType.Exponential,

                // Only the dependency being unwell is retried. The breaker's own refusal is not an RpcException, so it is not retried either:
                // sleeping through pauses for calls that cannot succeed turned an instant refusal into 1.4 s (ADR 0051, 0052).
                ShouldHandle = new PredicateBuilder().Handle<RpcException>(IsTransient),
                OnRetry = _ => Event("OnRetry"),
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = 0.5,
                SamplingDuration = settings.SamplingDuration,
                MinimumThroughput = settings.MinimumThroughput,
                BreakDuration = settings.BreakDuration,
                ShouldHandle = new PredicateBuilder().Handle<RpcException>(IsTransient),
                OnOpened = _ => Event("OnCircuitOpened"),
                OnHalfOpened = _ => Event("OnCircuitHalfOpened"),
                OnClosed = _ => Event("OnCircuitClosed"),
            })
            .Build();
    }
}

/// <summary>Runs every unary call through the pipeline, one deadline per attempt. One instance holds the breaker, so it must be a singleton.</summary>
public sealed class DirectoryGrpcResilienceInterceptor(ResiliencePipeline pipeline, DirectoryGrpcResilienceSettings settings) : Interceptor
{
    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        var response = RunAsync(request, context, continuation);

        return new AsyncUnaryCall<TResponse>(
            response,
            Task.FromResult(new Metadata()),
            () => response.IsCompletedSuccessfully ? Status.DefaultSuccess : new Status(StatusCode.Unavailable, "the call did not succeed"),
            () => new Metadata(),
            () => { });
    }

    private async Task<TResponse> RunAsync<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
        where TRequest : class
        where TResponse : class
    {
        try
        {
            return await pipeline.ExecuteAsync(
                async token =>
                {
                    var options = context.Options.WithCancellationToken(token).WithDeadline(DateTime.UtcNow + settings.AttemptTimeout);
                    try
                    {
                        return await continuation(request, new ClientInterceptorContext<TRequest, TResponse>(context.Method, context.Host, options))
                            .ResponseAsync;
                    }
                    catch (RpcException exception) when (exception.StatusCode == StatusCode.Cancelled && token.IsCancellationRequested)
                    {
                        // The total deadline fired: let the pipeline see a cancellation so that it reports a timeout.
                        throw new OperationCanceledException(token);
                    }
                },
                context.Options.CancellationToken);
        }
        catch (BrokenCircuitException exception)
        {
            throw new RpcException(new Status(StatusCode.Unavailable, "DirectoryService is not being called: the circuit is open"), exception.Message);
        }
        catch (TimeoutRejectedException)
        {
            throw new RpcException(new Status(StatusCode.DeadlineExceeded, "the call to DirectoryService did not finish within its total deadline"));
        }
    }
}
