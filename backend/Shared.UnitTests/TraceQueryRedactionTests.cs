using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Shared.Observability;

namespace Shared.UnitTests;

// NotificationService's hub takes the OAuth token as ?access_token= (a browser WebSocket cannot set a header). The
// incoming-request span records the query string, so the token would be written to the trace backend unless the
// instrumentation redacts it (it does, by default - this pins that). Runs a real request through the real instrumentation and reads what the
// exported span says.
// Listens to every activity in the process, so it must not run beside a test that hosts a real web server (its list would change under it).
[Collection("ProcessWideActivities")]
public class TraceQueryRedactionTests
{
    private const string Secret = "eyJ-SECRET-TOKEN-VALUE";

    private sealed class Collect(List<Activity> spans) : BaseExporter<Activity>
    {
        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var activity in batch)
            {
                spans.Add(activity);
            }

            return ExportResult.Success;
        }
    }

    private static async Task<Activity> SpanFor(string pathAndQuery)
    {
        var spans = new List<Activity>();
        using var tracing = Sdk.CreateTracerProviderBuilder()
            .AddAspNetCoreInstrumentation(ObservabilityExtensions.ConfigureAspNetCore)
            .AddProcessor(new SimpleActivityExportProcessor(new Collect(spans)))
            .Build();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.MapGet("/{**rest}", () => Results.Ok());
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            using var http = new HttpClient { BaseAddress = new Uri(address) };
            (await http.GetAsync(pathAndQuery)).EnsureSuccessStatusCode();
        }
        finally
        {
            await app.StopAsync();
        }

        tracing.ForceFlush();
        return Assert.Single(spans);
    }

    private static string Everything(Activity span) =>
        string.Join(" | ", span.TagObjects.Select(t => $"{t.Key}={t.Value}").Append(span.DisplayName));

    [Fact]
    public async Task The_access_token_in_a_query_string_never_reaches_the_span()
    {
        var span = await SpanFor($"/hub/notifications?id=abc&access_token={Secret}");

        Assert.DoesNotContain(Secret, Everything(span));
    }

    // The instrumentation redacts every query value by default (it keeps the names), which is what keeps the token
    // out. Asserted by value so that a configuration or version change that turns redaction off is seen here.
    [Fact]
    public async Task Parameter_names_are_kept_and_every_value_is_redacted()
    {
        var span = await SpanFor($"/hub/notifications?id=abc&access_token={Secret}");

        Assert.Equal("?id=Redacted&access_token=Redacted", span.GetTagItem("url.query"));
    }
}
