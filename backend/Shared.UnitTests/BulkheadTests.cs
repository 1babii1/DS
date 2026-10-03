using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shared.Resilience;
using Xunit.Abstractions;

namespace Shared.UnitTests;

// Bulkheads (ADR 0045): one slow kind of request (here a 300 ms "report") floods a service that also does quick work. Without a class of its
// own the slow work fills the overall limit and the quick work is turned away or waits; with a small class for it, the slow work fills only its
// own compartment and the quick work does not notice.
[Collection("ProcessWideActivities")]
public class BulkheadTests(ITestOutputHelper output)
{
    private static async Task<(WebApplication App, string Url)> Start(bool bulkhead)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var settings = new Dictionary<string, string?>
        {
            ["LoadShedding:MaxConcurrentRequests"] = "8",
            ["LoadShedding:QueueLimit"] = "8",
            ["LoadShedding:MaxQueueWait"] = "00:00:00.150",
        };
        if (bulkhead)
        {
            settings["LoadShedding:Classes:reports:PathPrefixes:0"] = "/api/reports";
            settings["LoadShedding:Classes:reports:MaxConcurrentRequests"] = "3";
            settings["LoadShedding:Classes:reports:QueueLimit"] = "3";
        }

        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddLoadShedding(builder.Configuration);
        var app = builder.Build();
        app.UseLoadShedding();
        app.MapGet("/api/reports/{id}", async () =>
        {
            await Task.Delay(300);
            return Results.Ok();
        });
        app.MapGet("/api/quick", async () =>
        {
            await Task.Delay(10);
            return Results.Ok();
        });
        await app.StartAsync();
        return (app, app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
    }

    private sealed record Result(int Ok, int Shed, double P95Ms);

    private static async Task<Result> Quick(string url, TimeSpan duration)
    {
        using var http = new HttpClient { BaseAddress = new Uri(url) };
        var latencies = new List<double>();
        var shed = 0;
        var stop = Stopwatch.StartNew();
        while (stop.Elapsed < duration)
        {
            var watch = Stopwatch.StartNew();
            using var response = await http.GetAsync("/api/quick");
            if (response.StatusCode == HttpStatusCode.OK)
            {
                latencies.Add(watch.Elapsed.TotalMilliseconds);
            }
            else
            {
                shed++;
            }

            await Task.Delay(20);
        }

        latencies.Sort();
        return new Result(latencies.Count, shed, latencies.Count == 0 ? 0 : latencies[(int)(latencies.Count * 0.95)]);
    }

    private static Task Flood(string url, TimeSpan duration)
    {
        var http = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = 60 }) { BaseAddress = new Uri(url) };
        var stop = Stopwatch.StartNew();
        return Task.WhenAll(Enumerable.Range(0, 40).Select(i => Task.Run(async () =>
        {
            while (stop.Elapsed < duration)
            {
                using var response = await http.GetAsync($"/api/reports/{i}");
                if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
                {
                    await Task.Delay(30);
                }
            }
        })));
    }

    [Fact]
    public async Task A_flood_of_slow_requests_does_not_take_the_places_of_the_quick_ones_when_it_has_a_class_of_its_own()
    {
        var (plain, plainUrl) = await Start(bulkhead: false);
        var (walled, walledUrl) = await Start(bulkhead: true);
        try
        {
            var duration = TimeSpan.FromSeconds(4);
            var floodPlain = Flood(plainUrl, duration);
            var withoutWall = await Quick(plainUrl, duration);
            await floodPlain;

            var floodWalled = Flood(walledUrl, duration);
            var withWall = await Quick(walledUrl, duration);
            await floodWalled;

            var shed = walled.Services.GetRequiredService<LoadShedder>().ShedBy;
            output.WriteLine($"no class:   quick ok={withoutWall.Ok} shed={withoutWall.Shed} p95={withoutWall.P95Ms:F0} ms");
            output.WriteLine($"with class: quick ok={withWall.Ok} shed={withWall.Shed} p95={withWall.P95Ms:F0} ms; refused by {string.Join(", ", shed.Select(s => $"{s.Key}={s.Value}"))}");

            Assert.True(withoutWall.Shed > 0 || withoutWall.P95Ms > 100, "without a class the slow flood did not hurt the quick requests; the experiment shows nothing");
            Assert.Equal(0, withWall.Shed);
            // What matters is who got served: without a class the quick requests were refused almost always; with one, never. (Their latency
            // is not compared: without the class only the few that got through have one, and a loaded runner makes any fixed number wrong.)
            Assert.True(withWall.Ok > 3 * Math.Max(1, withoutWall.Ok), $"quick requests served: {withWall.Ok} with the class, {withoutWall.Ok} without");
            Assert.True(shed.GetValueOrDefault("reports") > 0, "the reports class turned nothing away");
            Assert.Equal(0, shed.GetValueOrDefault(LoadShedder.Overall));
        }
        finally
        {
            await plain.DisposeAsync();
            await walled.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_request_outside_every_class_is_limited_only_by_the_overall_limit()
    {
        var (app, url) = await Start(bulkhead: true);
        try
        {
            var result = await Quick(url, TimeSpan.FromSeconds(1));

            Assert.Equal(0, result.Shed);
            Assert.True(result.Ok > 2);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }
}
