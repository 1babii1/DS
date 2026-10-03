using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared.Resilience;
using Xunit.Abstractions;

namespace Shared.UnitTests;

// Load shedding (ADR 0038), measured on a real Kestrel in this process. The endpoint does 50 ms of work through a gate that lets 8
// requests through at a time, the way a database connection pool would. Eighty clients ask back to back for a few seconds:
// ten times what the gate can serve.
[Collection("ProcessWideActivities")]
public class LoadSheddingTests(ITestOutputHelper output)
{
    private sealed record Outcome(int Ok, int Shed, double OkP95Ms, string? RetryAfter, long ServerShed);

    private static async Task<(WebApplication App, string Url)> Start(bool shedding)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        if (shedding)
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LoadShedding:MaxConcurrentRequests"] = "8",
                ["LoadShedding:QueueLimit"] = "8",
                ["LoadShedding:MaxQueueWait"] = "00:00:00.150",
            });
            builder.Services.AddLoadShedding(builder.Configuration);
        }

        var app = builder.Build();
        if (shedding)
        {
            app.UseLoadShedding();
        }

        var gate = new SemaphoreSlim(8);
        app.MapGet("/work", async () =>
        {
            await gate.WaitAsync();
            try
            {
                await Task.Delay(50);
                return Results.Ok();
            }
            finally
            {
                gate.Release();
            }
        });
        app.MapGet("/health/live", () => Results.Ok());
        await app.StartAsync();
        var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return (app, url);
    }

    private static async Task<Outcome> Hammer(string url, string path, int clients, TimeSpan duration)
    {
        using var http = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = clients }) { BaseAddress = new Uri(url) };
        var okLatencies = new List<double>();
        var ok = 0;
        var shed = 0;
        string? retryAfter = null;
        var stop = Stopwatch.StartNew();

        await Task.WhenAll(Enumerable.Range(0, clients).Select(_ => Task.Run(async () =>
        {
            while (stop.Elapsed < duration)
            {
                var watch = Stopwatch.StartNew();
                using var response = await http.GetAsync(path);
                var took = watch.Elapsed.TotalMilliseconds;
                lock (okLatencies)
                {
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        ok++;
                        okLatencies.Add(took);
                    }
                    else if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
                    {
                        shed++;
                        retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    }
                }

                if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
                {
                    await Task.Delay(20);
                }
            }
        })));

        okLatencies.Sort();
        var p95 = okLatencies.Count == 0 ? 0 : okLatencies[(int)(okLatencies.Count * 0.95)];
        return new Outcome(ok, shed, p95, retryAfter, 0);
    }

    [Fact]
    public async Task At_ten_times_capacity_the_accepted_requests_stay_fast_and_the_rest_are_turned_away_with_a_retry_hint()
    {
        var (plain, plainUrl) = await Start(shedding: false);
        var (guarded, guardedUrl) = await Start(shedding: true);
        try
        {
            var without = await Hammer(plainUrl, "/work", clients: 80, TimeSpan.FromSeconds(4));
            var with = await Hammer(guardedUrl, "/work", clients: 80, TimeSpan.FromSeconds(4));
            var shedCount = guarded.Services.GetRequiredService<LoadShedder>().ShedCount;

            output.WriteLine($"without shedding: ok={without.Ok} shed={without.Shed} accepted p95={without.OkP95Ms:F0} ms");
            output.WriteLine($"with shedding:    ok={with.Ok} shed={with.Shed} accepted p95={with.OkP95Ms:F0} ms, server counted {shedCount} shed");

            Assert.Equal(0, without.Shed);
            Assert.True(without.OkP95Ms > 300, $"the unprotected service was not slowed by the load ({without.OkP95Ms:F0} ms); the experiment shows nothing");
            Assert.True(with.Shed > 0, "an overloaded protected service must turn some requests away");
            Assert.Equal("1", with.RetryAfter);
            Assert.True(with.OkP95Ms < without.OkP95Ms / 2, $"accepted p95 {with.OkP95Ms:F0} ms was not clearly better than {without.OkP95Ms:F0} ms");
            Assert.True(with.Ok > 0);
            Assert.Equal(with.Shed, shedCount);
        }
        finally
        {
            await plain.DisposeAsync();
            await guarded.DisposeAsync();
        }
    }

    [Fact]
    public async Task Below_capacity_nothing_is_turned_away()
    {
        var (guarded, url) = await Start(shedding: true);
        try
        {
            var result = await Hammer(url, "/work", clients: 6, TimeSpan.FromSeconds(1));

            Assert.Equal(0, result.Shed);
            Assert.True(result.Ok > 20);
        }
        finally
        {
            await guarded.DisposeAsync();
        }
    }

    [Fact]
    public async Task The_health_probe_is_never_shed_even_when_everything_else_is()
    {
        var (guarded, url) = await Start(shedding: true);
        try
        {
            var load = Hammer(url, "/work", clients: 80, TimeSpan.FromSeconds(2));
            await Task.Delay(500);
            using var http = new HttpClient { BaseAddress = new Uri(url) };

            var statuses = new List<HttpStatusCode>();
            for (var i = 0; i < 20; i++)
            {
                statuses.Add((await http.GetAsync("/health/live")).StatusCode);
            }

            await load;
            Assert.All(statuses, s => Assert.Equal(HttpStatusCode.OK, s));
        }
        finally
        {
            await guarded.DisposeAsync();
        }
    }
}
