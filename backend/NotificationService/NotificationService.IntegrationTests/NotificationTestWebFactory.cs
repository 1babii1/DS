using System.Data.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NotificationService.Infrastructure.Postgres;
using NotificationService.Web;
using Npgsql;
using Respawn;
using Testcontainers.PostgreSql;

namespace NotificationService.IntegrationTests;

public class NotificationTestWebFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _dbContainer = new PostgreSqlBuilder()
        .WithImage("postgres:16")
        .WithDatabase("notification_service_db")
        .WithPassword("postgres")
        .WithUsername("postgres")
        .Build();

    private Respawner _respawner = null!;
    private DbConnection _dbConnection = null!;

    public async Task InitializeAsync()
    {
        await _dbContainer.StartAsync();

        _dbConnection = new NpgsqlConnection(_dbContainer.GetConnectionString());
        await _dbConnection.OpenAsync();

        await using var scope = Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NotificationDbContext>();
        await dbContext.Database.EnsureCreatedAsync();

        await InitializeRespawner();
    }

    public new async Task DisposeAsync()
    {
        if (_dbConnection is not null)
        {
            await _dbConnection.DisposeAsync();
        }

        await _dbContainer.DisposeAsync();
    }

    public async Task ResetDatabaseAsync() => await _respawner.ResetAsync(_dbConnection);

    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureTestServices(services =>
    {
        // DomainEventsConsumer/OutboxPublisher are IHostedServices - removing them means
        // tests construct and call the consumer directly instead of racing against the
        // host's own auto-started instance, which would try to connect to a broker that
        // doesn't exist in this test run.
        services.RemoveAll<IHostedService>();

        services.RemoveAll<DbContextOptions<NotificationDbContext>>();
        services.RemoveAll<NotificationDbContext>();
        services.AddDbContext<NotificationDbContext>(
            options => options.UseNpgsql(_dbContainer.GetConnectionString()));
    });

    private async Task InitializeRespawner()
    {
        _respawner = await Respawner.CreateAsync(
            _dbConnection,
            new RespawnerOptions { DbAdapter = DbAdapter.Postgres, SchemasToInclude = ["notification"] });
    }
}
