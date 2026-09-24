using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RewardsService.Infrastructure;
using Testcontainers.PostgreSql;

namespace ChaosTests;

// A real Postgres and a real single-node Kafka, with the ability to freeze either one.
//
// "Freeze" is `docker pause`: the processes stop but the TCP connections stay open. That is
// the nasty case - a hung broker or database, not a clean refusal - because nothing errors
// immediately; every caller just waits until its own timeout fires. It is the failure that
// retry, timeout and offset-commit logic is actually written for.
public sealed class ChaosStack : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("chaos")
        .WithPassword("postgres")
        .WithUsername("postgres")
        .Build();

    private IContainer? _kafka;

    public string BootstrapServers { get; private set; } = null!;

    public IServiceProvider Services { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        // The host port is chosen first and used for both sides of the mapping, because the
        // broker advertises its own address back to clients: a random mapped port would be
        // advertised as the container-internal one and clients could never reconnect.
        var port = FreePort();
        BootstrapServers = $"localhost:{port}";
        _kafka = new ContainerBuilder("apache/kafka:3.9.0")
            .WithPortBinding(port, port)
            .WithEnvironment("KAFKA_NODE_ID", "1")
            .WithEnvironment("KAFKA_PROCESS_ROLES", "broker,controller")
            .WithEnvironment("KAFKA_LISTENERS", $"HOST://0.0.0.0:{port},CONTROLLER://localhost:9093")
            .WithEnvironment("KAFKA_ADVERTISED_LISTENERS", $"HOST://localhost:{port}")
            .WithEnvironment("KAFKA_LISTENER_SECURITY_PROTOCOL_MAP", "HOST:PLAINTEXT,CONTROLLER:PLAINTEXT")
            .WithEnvironment("KAFKA_INTER_BROKER_LISTENER_NAME", "HOST")
            .WithEnvironment("KAFKA_CONTROLLER_LISTENER_NAMES", "CONTROLLER")
            .WithEnvironment("KAFKA_CONTROLLER_QUORUM_VOTERS", "1@localhost:9093")
            .WithEnvironment("KAFKA_OFFSETS_TOPIC_REPLICATION_FACTOR", "1")
            .WithEnvironment("KAFKA_TRANSACTION_STATE_LOG_REPLICATION_FACTOR", "1")
            .WithEnvironment("KAFKA_TRANSACTION_STATE_LOG_MIN_ISR", "1")
            .WithEnvironment("KAFKA_GROUP_INITIAL_REBALANCE_DELAY_MS", "0")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Kafka Server started"))
            .Build();
        await _kafka.StartAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        // Short client-side timeouts so a frozen database fails the way it does in production, after the
        // configured timeout, instead of after Npgsql's 30s default that would make this test take minutes.
        services.AddDbContext<RewardsDbContext>(o => o.UseNpgsql(_postgres.GetConnectionString() + ";Command Timeout=3;Timeout=3"));
        Services = services.BuildServiceProvider();

        await using var scope = Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<RewardsDbContext>().Database.EnsureCreatedAsync();
    }

    public Task FreezeKafkaAsync() => Docker("pause", _kafka!.Id);

    public Task ThawKafkaAsync() => Docker("unpause", _kafka!.Id);

    public Task FreezePostgresAsync() => Docker("pause", _postgres.Id);

    public Task ThawPostgresAsync() => Docker("unpause", _postgres.Id);

    public async Task DisposeAsync()
    {
        // A paused container cannot be stopped cleanly; make sure nothing is left frozen.
        if (_kafka is not null)
        {
            await Docker("unpause", _kafka.Id, ignoreFailure: true);
            await _kafka.DisposeAsync();
        }

        await Docker("unpause", _postgres.Id, ignoreFailure: true);
        await _postgres.DisposeAsync();
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task Docker(string verb, string containerId, bool ignoreFailure = false)
    {
        using var process = Process.Start(new ProcessStartInfo("docker", $"{verb} {containerId}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        await process.WaitForExitAsync();
        if (process.ExitCode != 0 && !ignoreFailure)
        {
            throw new InvalidOperationException($"docker {verb} failed: {await process.StandardError.ReadToEndAsync()}");
        }
    }
}
