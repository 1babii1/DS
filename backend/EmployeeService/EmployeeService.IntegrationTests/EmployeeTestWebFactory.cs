using System.Data.Common;
using EmployeeService.Application.Directory;
using EmployeeService.Infrastructure.Postgres;
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

namespace EmployeeService.IntegrationTests;

public class EmployeeTestWebFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    // Plain postgres is enough here: unlike DirectoryService, this model needs neither
    // ltree nor vector.
    private readonly PostgreSqlContainer _dbContainer = new PostgreSqlBuilder()
        .WithImage("postgres:16")
        .WithDatabase("employee_service_db")
        .WithPassword("postgres")
        .WithUsername("postgres")
        .Build();

    private Respawner _respawner = null!;
    private DbConnection _dbConnection = null!;

    public FakeDirectoryLookupClient DirectoryLookup { get; } = new();

    public async Task InitializeAsync()
    {
        await _dbContainer.StartAsync();

        _dbConnection = new NpgsqlConnection(_dbContainer.GetConnectionString());
        await _dbConnection.OpenAsync();

        await using var scope = Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<EmployeeDbContext>();

        // EnsureCreated builds the schema straight from the current EF model, xmin
        // concurrency mapping included - no need to replay migration files in tests.
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

    public async Task ResetDatabaseAsync()
    {
        await _respawner.ResetAsync(_dbConnection);
        DirectoryLookup.NextValidation = FakeDirectoryLookupClient.ValidAssignment;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureTestServices(services =>
    {
        // Background services (the outbox publisher) need Kafka, which isn't running
        // for these tests and isn't what they're testing - same reasoning as
        // DirectoryService's test factory.
        services.RemoveAll<IHostedService>();

        // EmployeeDbContext only exposes the standard DbContextOptions<T> constructor
        // (no raw-connection-string overload like DirectoryServiceDbContext), so the
        // registration itself needs replacing rather than just the connection string
        // argument to a custom constructor.
        services.RemoveAll<DbContextOptions<EmployeeDbContext>>();
        services.RemoveAll<EmployeeDbContext>();
        services.AddDbContext<EmployeeDbContext>(options => options.UseNpgsql(_dbContainer.GetConnectionString()));

        services.RemoveAll<IDirectoryLookupClient>();
        services.AddSingleton<IDirectoryLookupClient>(DirectoryLookup);
    });

    private async Task InitializeRespawner()
    {
        _respawner = await Respawner.CreateAsync(
            _dbConnection,
            new RespawnerOptions { DbAdapter = DbAdapter.Postgres, SchemasToInclude = ["employee"] });
    }
}
