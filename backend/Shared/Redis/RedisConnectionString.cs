namespace Shared.Redis;

/// <summary>
/// Makes a Redis connection string fail fast. Redis here is an accelerator and a message relay, never a source of truth, so a
/// dead Redis must cost a request a few hundred milliseconds at worst, not the client's default of several seconds per call
/// (ADR 0036). Measured: with Redis unreachable and the defaults, every cached read took about 6 s.
/// - <c>abortConnect=false</c>: start and run without a connection; reconnect in the background instead of failing the first call
///   and retrying the connect on every call after it.
/// - short connect and operation timeouts: a call that cannot be served quickly gives up and the caller goes to the source.
/// Anything the connection string already sets is left as it is.
/// </summary>
public static class RedisConnectionString
{
    private static readonly (string Key, string Value)[] Defaults =
    [
        ("abortConnect", "false"),
        ("connectTimeout", "500"),
        ("syncTimeout", "500"),
        ("asyncTimeout", "500"),
        ("connectRetry", "2"),
    ];

    public static string Resilient(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var present = connectionString.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2)[0])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var additions = Defaults.Where(d => !present.Contains(d.Key)).Select(d => $"{d.Key}={d.Value}");
        return string.Join(',', new[] { connectionString.TrimEnd(',') }.Concat(additions));
    }
}
