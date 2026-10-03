using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shared.FeatureFlags;

// Loads feature flags the way a service does (the file named by FEATURE_FLAGS_FILE, watched while running) and prints the answer for
// a few users once a second. Run in a pod with the feature-flags ConfigMap mounted, it shows how long an edit to the ConfigMap takes
// to reach a running process (ADR 0041).
var builder = Host.CreateApplicationBuilder();
builder.Configuration.AddFeatureFlagsFile();
builder.Services.AddFeatureFlags(builder.Configuration);
using var host = builder.Build();
await host.StartAsync();

var flags = host.Services.GetRequiredService<IFeatureFlags>();
var started = DateTime.UtcNow;
string? last = null;
while ((DateTime.UtcNow - started).TotalSeconds < int.Parse(args.FirstOrDefault() ?? "180"))
{
    var kill = await flags.IsEnabledAsync("search-hybrid-default", "user-1", defaultValue: true);
    var state = kill ? "ON" : "OFF";
    if (state != last)
    {
        Console.WriteLine($"{(DateTime.UtcNow - started).TotalSeconds:F0}s search-hybrid-default for user-1: {state}");
        last = state;
    }

    await Task.Delay(500);
}
