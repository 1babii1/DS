using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenFeature;
using OpenFeature.Model;

namespace Shared.FeatureFlags;

/// <summary>What the rest of the code asks: is this flag on for this user. The default says what happens when the flag is not configured.</summary>
public interface IFeatureFlags
{
    ValueTask<bool> IsEnabledAsync(string flag, string? userKey, bool defaultValue, CancellationToken cancellationToken = default);
}

public sealed class OpenFeatureFlags : IFeatureFlags
{
    private readonly FeatureClient _client = Api.Instance.GetClient("platform");

    public async ValueTask<bool> IsEnabledAsync(string flag, string? userKey, bool defaultValue, CancellationToken cancellationToken = default)
    {
        var context = userKey is null ? EvaluationContext.Empty : EvaluationContext.Builder().SetTargetingKey(userKey).Build();
        return await _client.GetBooleanValueAsync(flag, defaultValue, context, cancellationToken: cancellationToken);
    }
}

public static class FeatureFlagsExtensions
{
    /// <summary>Flags from the <c>FeatureFlags</c> configuration section, reloaded while the service runs (ADR 0041).</summary>
    public static IServiceCollection AddFeatureFlags(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<FeatureFlagsOptions>(configuration.GetSection(FeatureFlagsOptions.SectionName));
        services.AddSingleton<ConfigurationFeatureProvider>();
        services.AddHostedService<FeatureProviderRegistration>();
        services.AddSingleton<IFeatureFlags, OpenFeatureFlags>();
        return services;
    }
}

// The provider is registered with OpenFeature's process-wide API before the host starts serving, so that no request sees an
// unset provider (which would answer every flag with its default).
internal sealed class FeatureProviderRegistration(ConfigurationFeatureProvider provider) : IHostedLifecycleService
{
    public async Task StartingAsync(CancellationToken cancellationToken) =>
        await Api.Instance.SetProviderAsync("platform", provider);

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class FeatureFlagsFileExtensions
{
    public const string FileVariable = "FEATURE_FLAGS_FILE";

    public const string DefaultFile = "/etc/platform/flags/flags.json";

    /// <summary>
    /// Reads flags from a JSON file (<c>{"FeatureFlags": {"name": {"Enabled": true, "Percentage": 50}}}</c>) that may appear, change
    /// or disappear while the service runs. In a cluster the file is a mounted ConfigMap: editing the ConfigMap changes the flag in a
    /// running pod within a minute or so, with no deploy. The path is in <c>FEATURE_FLAGS_FILE</c>; a missing file is not an error.
    /// </summary>
    public static IConfigurationBuilder AddFeatureFlagsFile(this IConfigurationBuilder configuration, string? path = null)
    {
        path ??= Environment.GetEnvironmentVariable(FileVariable) ?? DefaultFile;
        var directory = Path.GetDirectoryName(path);

        // Without the directory there is nothing to watch (a mounted ConfigMap always brings its directory with it): no flags from
        // a file, and the rest of the configuration still applies.
        if (directory is null || !Directory.Exists(directory))
        {
            return configuration;
        }

        // Polling, because a ConfigMap is updated by swapping a symlink, which an inotify watch on the file does not see.
        var files = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(directory)
        {
            UsePollingFileWatcher = true,
            UseActivePolling = true,
        };
        return configuration.AddJsonFile(files, Path.GetFileName(path), optional: true, reloadOnChange: true);
    }
}
