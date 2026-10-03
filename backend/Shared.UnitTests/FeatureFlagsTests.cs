using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenFeature.Model;
using Shared.FeatureFlags;

namespace Shared.UnitTests;

// Feature flags (ADR 0041): a kill switch and a sticky percentage rollout, read from configuration and reloaded while running.
public class FeatureFlagsTests
{
    private sealed class Settings(FeatureFlagsOptions value) : IOptionsMonitor<FeatureFlagsOptions>
    {
        public FeatureFlagsOptions CurrentValue => value;

        public FeatureFlagsOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<FeatureFlagsOptions, string?> listener) => null;
    }

    private sealed class ReloadableSource : IConfigurationSource
    {
        public ReloadableProvider Provider { get; } = new();

        public IConfigurationProvider Build(IConfigurationBuilder builder) => Provider;
    }

    private sealed class ReloadableProvider : ConfigurationProvider
    {
        public void Replace(Dictionary<string, string?> data)
        {
            Data = data;
            OnReload();
        }
    }

    private static ConfigurationFeatureProvider Provider(params (string Key, bool Enabled, int Percentage)[] flags)
    {
        var options = new FeatureFlagsOptions();
        foreach (var (key, enabled, percentage) in flags)
        {
            options[key] = new FlagSetting { Enabled = enabled, Percentage = percentage };
        }

        return new ConfigurationFeatureProvider(new Settings(options));
    }

    private static EvaluationContext User(string key) => EvaluationContext.Builder().SetTargetingKey(key).Build();

    private static async Task<bool> On(ConfigurationFeatureProvider provider, string flag, string? user = null) =>
        (await provider.ResolveBooleanValueAsync(flag, false, user is null ? null : User(user))).Value;

    [Fact]
    public async Task A_flag_nobody_configured_gives_the_default_the_caller_chose()
    {
        var provider = Provider();

        var asked = await provider.ResolveBooleanValueAsync("unknown", true);

        Assert.True(asked.Value);
        Assert.Equal(OpenFeature.Constant.ErrorType.FlagNotFound, asked.ErrorType);
    }

    [Fact]
    public async Task The_kill_switch_turns_a_flag_off_for_everyone_whatever_the_percentage()
    {
        var provider = Provider(("f", false, 100));

        Assert.False(await On(provider, "f", "alice"));
        Assert.False(await On(provider, "f"));
    }

    [Fact]
    public async Task A_flag_at_one_hundred_percent_is_on_even_for_someone_nobody_can_identify()
    {
        var provider = Provider(("f", true, 100));

        Assert.True(await On(provider, "f"));
        Assert.True(await On(provider, "f", "alice"));
    }

    [Fact]
    public async Task A_partial_rollout_cannot_place_someone_unidentified_and_keeps_them_on_the_safe_side()
    {
        var provider = Provider(("f", true, 50));

        Assert.False(await On(provider, "f"));
    }

    [Fact]
    public async Task A_partial_rollout_reaches_about_that_share_of_users()
    {
        var provider = Provider(("f", true, 25));
        var on = 0;
        for (var i = 0; i < 10_000; i++)
        {
            if (await On(provider, "f", $"user-{i}"))
            {
                on++;
            }
        }

        Assert.InRange(on, 2_300, 2_700);
    }

    [Fact]
    public async Task A_user_keeps_their_answer_and_everyone_already_in_stays_in_as_the_percentage_rises()
    {
        var small = Provider(("f", true, 20));
        var large = Provider(("f", true, 40));
        var insideSmall = 0;

        for (var i = 0; i < 5_000; i++)
        {
            var user = $"user-{i}";
            var first = await On(small, "f", user);
            Assert.Equal(first, await On(small, "f", user));
            if (first)
            {
                insideSmall++;
                Assert.True(await On(large, "f", user), $"{user} was in at 20% and out at 40%");
            }
        }

        Assert.True(insideSmall > 0);
    }

    [Fact]
    public async Task Two_flags_do_not_pick_the_same_users()
    {
        var provider = Provider(("a", true, 10), ("b", true, 10));
        var both = 0;
        var either = 0;
        for (var i = 0; i < 10_000; i++)
        {
            var a = await On(provider, "a", $"user-{i}");
            var b = await On(provider, "b", $"user-{i}");
            both += a && b ? 1 : 0;
            either += a || b ? 1 : 0;
        }

        // Independent 10% draws overlap about 1% of the time; the same users in both would overlap 10%.
        Assert.True(both < 300, $"{both} users were in both");
        Assert.True(either > 1_600);
    }

    [Fact]
    public async Task A_change_in_configuration_reaches_a_running_provider_without_a_restart()
    {
        var source = new ReloadableSource();
        source.Provider.Replace(new Dictionary<string, string?> { ["FeatureFlags:f:Enabled"] = "true", ["FeatureFlags:f:Percentage"] = "100" });
        var configuration = new ConfigurationBuilder().Add(source).Build();
        var services = new ServiceCollection();
        services.AddFeatureFlags(configuration);
        await using var provider = services.BuildServiceProvider();
        var flags = provider.GetRequiredService<ConfigurationFeatureProvider>();
        Assert.True(await On(flags, "f", "alice"));

        // What a file with reloadOnChange, or a ConfigMap mounted into the pod, does when it is edited.
        source.Provider.Replace(new Dictionary<string, string?> { ["FeatureFlags:f:Enabled"] = "false", ["FeatureFlags:f:Percentage"] = "100" });

        Assert.False(await On(flags, "f", "alice"));
    }

    [Fact]
    public async Task Asked_through_the_application_interface_the_flag_answers_for_the_user_it_is_asked_about()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["FeatureFlags:search-hybrid-default:Enabled"] = "true";
        builder.Configuration["FeatureFlags:search-hybrid-default:Percentage"] = "30";
        builder.Services.AddFeatureFlags(builder.Configuration);
        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var flags = host.Services.GetRequiredService<IFeatureFlags>();
            var inside = Enumerable.Range(0, 200).Select(i => $"user-{i}")
                .First(u => ConfigurationFeatureProvider.Bucket("search-hybrid-default", u) < 30);
            var outside = Enumerable.Range(0, 200).Select(i => $"user-{i}")
                .First(u => ConfigurationFeatureProvider.Bucket("search-hybrid-default", u) >= 30);

            Assert.True(await flags.IsEnabledAsync("search-hybrid-default", inside, defaultValue: false));
            Assert.False(await flags.IsEnabledAsync("search-hybrid-default", outside, defaultValue: true));
            Assert.True(await flags.IsEnabledAsync("not-configured", inside, defaultValue: true));
        }
        finally
        {
            await host.StopAsync();
        }
    }
}

public class FeatureFlagsFileTests
{
    [Fact]
    public async Task A_flag_file_that_is_edited_while_the_service_runs_changes_the_answer()
    {
        var directory = Directory.CreateTempSubdirectory("flags-").FullName;
        var file = Path.Combine(directory, "flags.json");
        await File.WriteAllTextAsync(file, """{"FeatureFlags":{"f":{"Enabled":true,"Percentage":100}}}""");
        try
        {
            var configuration = new ConfigurationBuilder().AddFeatureFlagsFile(file).Build();
            var services = new ServiceCollection();
            services.AddFeatureFlags(configuration);
            await using var provider = services.BuildServiceProvider();
            var flags = provider.GetRequiredService<ConfigurationFeatureProvider>();
            Assert.True((await flags.ResolveBooleanValueAsync("f", false)).Value);

            await File.WriteAllTextAsync(file, """{"FeatureFlags":{"f":{"Enabled":false,"Percentage":100}}}""");

            var off = false;
            for (var i = 0; i < 100 && !off; i++)
            {
                await Task.Delay(100);
                off = !(await flags.ResolveBooleanValueAsync("f", true)).Value;
            }

            Assert.True(off, "the edit to the file did not reach the running provider within ten seconds");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void A_missing_flag_file_is_not_an_error()
    {
        var configuration = new ConfigurationBuilder().AddFeatureFlagsFile("/no/such/directory/flags.json").Build();

        Assert.Empty(configuration.GetSection("FeatureFlags").GetChildren());
    }
}
