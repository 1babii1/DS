using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared.HealthChecks;
using Shared.Outbox;

namespace Shared.UnitTests;

// Readiness decides whether a Kubernetes Service sends traffic to an instance (ADR 0040). Kafka being away must not make an
// instance unready: none of these services needs Kafka to answer an HTTP request.
[Collection("ProcessWideActivities")]
public class KafkaHealthCheckTests
{
    [Fact]
    public async Task An_unreachable_broker_is_degraded_not_unhealthy_and_is_reported_in_time_for_a_probe()
    {
        var check = new KafkaHealthCheck("127.0.0.1:1", new KafkaSecurityOptions(null, null));

        var watch = Stopwatch.StartNew();
        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
        // The check gives up after 2 s; a loaded runner adds the cost of building the client, so the bound is the probe-scale one, not the check's own.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(6), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task The_ready_endpoint_still_answers_200_with_kafka_down()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddKafkaHealthCheck("127.0.0.1:1", new KafkaSecurityOptions(null, null));
        var app = builder.Build();
        app.MapDefaultHealthChecks();
        await app.StartAsync();
        try
        {
            var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            using var http = new HttpClient { BaseAddress = new Uri(url) };

            var response = await http.GetAsync("/health/ready");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("Degraded", await response.Content.ReadAsStringAsync());
        }
        finally
        {
            await app.DisposeAsync();
        }
    }
}
