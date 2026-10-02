using System.Data;
using DirectoryService.Application.Database;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using Shared.Consistency;

namespace DirectoryService.Infrastructure.Postgres.Database;

/// <summary>
/// The connections the service reads with, built once: the primary, and the replica when one is configured
/// (<c>ConnectionStrings:DirectoryServiceReadDb</c>). Without a replica everything is the primary, as it always was.
/// </summary>
public sealed class DirectoryDataSources : IDisposable, IAsyncDisposable
{
    public DirectoryDataSources(IConfiguration configuration, ILoggerFactory loggerFactory)
    {
        Primary = Build(configuration.GetConnectionString("DirectoryServiceDb"), loggerFactory)
            ?? throw new InvalidOperationException("Connection string 'DirectoryServiceDb' is not configured.");
        Replica = Build(configuration.GetConnectionString("DirectoryServiceReadDb"), loggerFactory);
        ReplicaPosition = Replica is null ? null : new NpgsqlReplayPosition(Replica, loggerFactory.CreateLogger<NpgsqlReplayPosition>());
    }

    public NpgsqlDataSource Primary { get; }

    public NpgsqlDataSource? Replica { get; }

    public IReplayPosition? ReplicaPosition { get; }

    private static NpgsqlDataSource? Build(string? connectionString, ILoggerFactory loggerFactory)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.UseLoggerFactory(loggerFactory);
        return builder.Build();
    }

    public void Dispose()
    {
        Primary.Dispose();
        Replica?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await Primary.DisposeAsync();
        if (Replica is not null)
        {
            await Replica.DisposeAsync();
        }
    }
}

// Every read handler opens its connection here. With a replica configured, the read goes there unless the caller has
// presented the position of a write of its own that the replica has not replayed yet (ADR 0025).
public class NpgsqlConnectionFactory(
    DirectoryDataSources sources,
    ReadConsistencyContext consistency,
    TimeProvider clock) : IDbConnectionFactory
{
    private static readonly ReadRoutingOptions Options = new();

    public async Task<IDbConnection> CreateConnectionAsync(CancellationToken cancellationToken)
    {
        if (sources.Replica is null || sources.ReplicaPosition is null)
        {
            return await sources.Primary.OpenConnectionAsync(cancellationToken);
        }

        var target = await ReadRouter.ChooseAsync(
            consistency.MinLsn, sources.ReplicaPosition, Options, (d, ct) => Task.Delay(d, ct), clock, cancellationToken);

        if (target == ReadTarget.Replica)
        {
            try
            {
                return await sources.Replica.OpenConnectionAsync(cancellationToken);
            }
            catch (NpgsqlException)
            {
                // The replica went away between the check and the connection: the primary can always answer.
            }
        }

        return await sources.Primary.OpenConnectionAsync(cancellationToken);
    }
}
