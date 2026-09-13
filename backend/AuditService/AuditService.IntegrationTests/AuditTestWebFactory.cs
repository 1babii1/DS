using System.Data.Common;
using AuditService.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Respawn;
using Testcontainers.PostgreSql;

namespace AuditService.IntegrationTests;

public class AuditTestWebFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _dbContainer = new PostgreSqlBuilder()
        .WithImage("postgres:16")
        .WithDatabase("audit_service_db")
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
        var dbContext = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
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
        // AuditConsumer itself is an IHostedService - removing it means tests construct
        // and call it directly instead of racing against the host's own auto-started
        // instance, which would try (and, with no Kafka here, keep retrying) to connect
        // to a broker that doesn't exist in this test run.
        services.RemoveAll<IHostedService>();

        services.RemoveAll<DbContextOptions<AuditDbContext>>();
        services.RemoveAll<AuditDbContext>();
        services.AddDbContext<AuditDbContext>(options => options.UseNpgsql(_dbContainer.GetConnectionString()));
    });

    private async Task InitializeRespawner()
    {
        _respawner = await Respawner.CreateAsync(
            _dbConnection,
            new RespawnerOptions { DbAdapter = DbAdapter.Postgres, SchemasToInclude = ["audit"] });
    }
}
