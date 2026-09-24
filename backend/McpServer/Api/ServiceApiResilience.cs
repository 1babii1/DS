using System.Net;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace McpServer.Api;

// What is retried and what trips the breaker, stated explicitly instead of left to defaults.
//
// Only real service trouble counts: network failures, timeouts and 5xx. A 429 does not - it is one
// caller being rate limited (DirectoryService limits per source IP, and every user arrives here
// from McpServer's IP), and counting it would let one heavy user open the breaker, or be retried
// into a worse bucket, for everyone. Reads only, so retrying is safe.
public static class ServiceApiResilience
{
    public static void Configure(ResiliencePipelineBuilder<HttpResponseMessage> builder)
    {
        var transientTrouble = new PredicateBuilder<HttpResponseMessage>()
            .Handle<HttpRequestException>()
            .Handle<TimeoutRejectedException>()
            .HandleResult(r => (int)r.StatusCode >= 500 || r.StatusCode == HttpStatusCode.RequestTimeout);

        builder.AddRetry(new RetryStrategyOptions<HttpResponseMessage>
        {
            ShouldHandle = transientTrouble,
            MaxRetryAttempts = 2,
            Delay = TimeSpan.FromMilliseconds(200),
            BackoffType = DelayBackoffType.Exponential,
        });

        builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
        {
            ShouldHandle = transientTrouble,
            FailureRatio = 0.5,
            SamplingDuration = TimeSpan.FromSeconds(30),
            MinimumThroughput = 5,
            BreakDuration = TimeSpan.FromSeconds(15),
        });

        // Per attempt, innermost: a tool call is someone waiting.
        builder.AddTimeout(TimeSpan.FromSeconds(10));
    }
}
