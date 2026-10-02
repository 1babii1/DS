using Microsoft.Extensions.Logging;
using Npgsql;

namespace Shared.Consistency;

public enum ReadTarget
{
    Primary,
    Replica,
}

/// <summary>What a request has seen of the database: the position of the latest write it (or its session) made, if any.</summary>
public sealed class ReadConsistencyContext
{
    public ulong? MinLsn { get; set; }
}

public sealed class ReadRoutingOptions
{
    /// <summary>How long a read waits for the replica to catch up to the caller's own write before reading the primary instead.</summary>
    public TimeSpan MaxWait { get; set; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(25);
}

/// <summary>How far the replica has replayed. Null when it cannot be asked (down, unreachable).</summary>
public interface IReplayPosition
{
    Task<ulong?> ReplayedAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Read-your-writes on a lagging replica (ADR 0025). A read with no token has seen nothing of its own and may be stale,
/// so it goes to the replica. A read that carries the position of its caller's last write goes to the replica only once
/// the replica has replayed that far; it waits a short, bounded time for that, then reads the primary rather than showing
/// the caller data from before their own change. A replica that cannot be asked is treated as behind: the primary answers.
/// </summary>
public static class ReadRouter
{
    public static async Task<ReadTarget> ChooseAsync(
        ulong? minLsn,
        IReplayPosition replica,
        ReadRoutingOptions options,
        Func<TimeSpan, CancellationToken, Task> delay,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (minLsn is null)
        {
            return ReadTarget.Replica;
        }

        var deadline = clock.GetUtcNow() + options.MaxWait;
        while (true)
        {
            var replayed = await replica.ReplayedAsync(cancellationToken);
            if (replayed is { } at && at >= minLsn)
            {
                return ReadTarget.Replica;
            }

            if (replayed is null || clock.GetUtcNow() >= deadline)
            {
                return ReadTarget.Primary;
            }

            await delay(options.PollInterval, cancellationToken);
        }
    }
}

public sealed class NpgsqlReplayPosition(NpgsqlDataSource replica, ILogger logger) : IReplayPosition
{
    public async Task<ulong?> ReplayedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var command = replica.CreateCommand("SELECT pg_last_wal_replay_lsn()::text");
            var text = (string?)await command.ExecuteScalarAsync(cancellationToken);
            return Lsn.TryParse(text, out var value) ? value : null;
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            logger.LogWarning(ex, "The read replica could not be asked how far it has replayed; reading the primary");
            return null;
        }
    }
}
