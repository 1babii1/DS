using Polly;
using Polly.CircuitBreaker;

namespace EmployeeService.Infrastructure.DirectoryGrpc;

/// <summary>The numbers of the policy on the gRPC call to DirectoryService; the defaults are what runs, tests pass smaller ones.</summary>
public sealed record DirectoryGrpcResilienceSettings
{
    public int MaxRetryAttempts { get; init; } = 3;

    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(200);

    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(3);

    public TimeSpan TotalTimeout { get; init; } = TimeSpan.FromSeconds(8);

    public int MinimumThroughput { get; init; } = 5;

    public TimeSpan BreakDuration { get; init; } = TimeSpan.FromSeconds(15);
}

public static class DirectoryGrpcResilience
{
    public static void Configure(ResiliencePipelineBuilder<HttpResponseMessage> builder, DirectoryGrpcResilienceSettings? settings = null)
    {
        settings ??= new DirectoryGrpcResilienceSettings();

        // Outermost: one deadline for the whole call, retries and their pauses included. Without it a call to a dependency that answers
        // nothing waited attempts x timeout plus pauses (21 s measured, ADR 0051).
        builder.AddTimeout(settings.TotalTimeout);

        builder.AddRetry(new()
        {
            MaxRetryAttempts = settings.MaxRetryAttempts,
            Delay = settings.RetryDelay,
            BackoffType = DelayBackoffType.Exponential,

            // A call the open breaker refused is not retried: nothing changes in the few hundred milliseconds of a pause, and sleeping
            // through three of them turned an instant refusal into 1.4 s (ADR 0051). Any other failure, a timeout included, is.
            ShouldHandle = new PredicateBuilder<HttpResponseMessage>().Handle<Exception>(exception => exception is not BrokenCircuitException),
        });

        builder.AddCircuitBreaker(new()
        {
            FailureRatio = 0.5,
            SamplingDuration = TimeSpan.FromSeconds(30),
            MinimumThroughput = settings.MinimumThroughput,
            BreakDuration = settings.BreakDuration,
        });

        builder.AddTimeout(settings.AttemptTimeout);
    }
}
