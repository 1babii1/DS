using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using OpenFeature;
using OpenFeature.Constant;
using OpenFeature.Model;

namespace Shared.FeatureFlags;

/// <summary>One flag as written in configuration: <c>FeatureFlags:&lt;key&gt;:Enabled</c> and <c>:Percentage</c>.</summary>
public sealed class FlagSetting
{
    /// <summary>The kill switch. False means off for everyone, whatever the percentage says.</summary>
    public bool Enabled { get; set; }

    /// <summary>The share of users (0 to 100) the flag is on for, when it is enabled. Chosen by a stable hash of the user, so
    /// the same user keeps the same answer as the percentage is raised, and everyone below the line stays in.</summary>
    public int Percentage { get; set; } = 100;
}

public sealed class FeatureFlagsOptions : Dictionary<string, FlagSetting>
{
    public const string SectionName = "FeatureFlags";
}

/// <summary>
/// An OpenFeature provider that reads flags from configuration (ADR 0041). In a cluster that is a ConfigMap: changing a flag is
/// an edit to configuration, picked up by a running service without a deploy (the options monitor reloads), which is the point of
/// separating "deployed" from "enabled". Boolean flags only; there is no use yet for the other kinds.
/// </summary>
public sealed class ConfigurationFeatureProvider(IOptionsMonitor<FeatureFlagsOptions> options) : FeatureProvider
{
    public override Metadata GetMetadata() => new("configuration");

    public override Task<ResolutionDetails<bool>> ResolveBooleanValueAsync(
        string flagKey, bool defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default)
    {
        if (!options.CurrentValue.TryGetValue(flagKey, out var flag))
        {
            return Task.FromResult(new ResolutionDetails<bool>(flagKey, defaultValue, OpenFeature.Constant.ErrorType.FlagNotFound, Reason.Error));
        }

        if (!flag.Enabled)
        {
            return Task.FromResult(new ResolutionDetails<bool>(flagKey, false, reason: Reason.Disabled));
        }

        if (flag.Percentage >= 100)
        {
            return Task.FromResult(new ResolutionDetails<bool>(flagKey, true, reason: Reason.Static));
        }

        var key = context?.TargetingKey;
        if (string.IsNullOrEmpty(key))
        {
            // A partial rollout needs someone to put on one side of the line; nobody to place means the safe side.
            return Task.FromResult(new ResolutionDetails<bool>(flagKey, false, reason: Reason.Default));
        }

        return Task.FromResult(new ResolutionDetails<bool>(flagKey, Bucket(flagKey, key) < flag.Percentage, reason: Reason.TargetingMatch));
    }

    /// <summary>A number from 0 to 99 that depends on the flag and the user and on nothing else. Keyed by flag as well, so that the
    /// users in the first ten percent of one flag are not always the first ten percent of every flag.</summary>
    public static int Bucket(string flagKey, string userKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{flagKey}:{userKey}"));
        return (int)(BitConverter.ToUInt32(hash, 0) % 100);
    }

    public override Task<ResolutionDetails<string>> ResolveStringValueAsync(
        string flagKey, string defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ResolutionDetails<string>(flagKey, defaultValue, OpenFeature.Constant.ErrorType.TypeMismatch, Reason.Error));

    public override Task<ResolutionDetails<int>> ResolveIntegerValueAsync(
        string flagKey, int defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ResolutionDetails<int>(flagKey, defaultValue, OpenFeature.Constant.ErrorType.TypeMismatch, Reason.Error));

    public override Task<ResolutionDetails<double>> ResolveDoubleValueAsync(
        string flagKey, double defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ResolutionDetails<double>(flagKey, defaultValue, OpenFeature.Constant.ErrorType.TypeMismatch, Reason.Error));

    public override Task<ResolutionDetails<Value>> ResolveStructureValueAsync(
        string flagKey, Value defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ResolutionDetails<Value>(flagKey, defaultValue, OpenFeature.Constant.ErrorType.TypeMismatch, Reason.Error));
}
