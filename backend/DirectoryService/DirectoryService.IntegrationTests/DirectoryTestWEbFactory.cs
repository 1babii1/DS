using System.Data.Common;
using DirectoryService.Infrastructure.Postgres;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Respawn;
using Testcontainers.PostgreSql;

namespace DirectoryService.IntegrationTests;

public class DirectoryTestWEbFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    // Same image as docker-compose: the model uses ltree for hierarchy paths and
    // vector for department embeddings, neither of which exists on a plain postgres image.
    private readonly PostgreSqlContainer _dbContainer = new PostgreSqlBuilder()
        .WithImage("pgvector/pgvector:pg18")
        .WithDatabase("directory_service_db")
        .WithPassword("postgres")
        .WithUsername("postgres")
        .Build();

    private Respawner _respawner = null!;
    private DbConnection _dbConection = null!;

    public async Task InitializeAsync()
    {
        await _dbContainer.StartAsync();

        _dbConection = new NpgsqlConnection(_dbContainer.GetConnectionString());
        await _dbConection.OpenAsync();

        // EnsureCreated does not emit CREATE EXTENSION, so the extensions the model
        // depends on have to exist before it runs. In Docker this is done by
        // docker/postgres/init-databases.sql.
        await using (var createExtensions = _dbConection.CreateCommand())
        {
            createExtensions.CommandText =
                "CREATE EXTENSION IF NOT EXISTS ltree; CREATE EXTENSION IF NOT EXISTS vector;";
            await createExtensions.ExecuteNonQueryAsync();
        }

        await using var scope = Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DirectoryServiceDbContext>();

        await dbContext.Database.EnsureCreatedAsync();

        await InitializeRespawner();
    }

    public new async Task DisposeAsync()
    {
        // Null when InitializeAsync threw. Without this guard the NRE here replaces the
        // real setup failure in the test output, which is how a broken container image
        // stayed hidden behind 24 identical NullReferenceExceptions.
        if (_dbConection is not null)
        {
            await _dbConection.DisposeAsync();
        }

        await _dbContainer.DisposeAsync();
    }

    public async Task ResetDatabaseAsync()
    {
        await _respawner.ResetAsync(_dbConection);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureTestServices(service =>
    {
        // Тесты вызывают хендлеры напрямую, фоновые сервисы в них не участвуют, но
        // мешают: воркер ретеншена удаляет из тех же таблиц, что чистит Respawn между
        // тестами, и два конкурирующих DELETE ловят deadlock, из-за чего падает
        // случайный тест. Воркеры эмбеддингов и outbox к тому же непрерывно логируют
        // ошибки, потому что Ollama и Kafka в тестовом окружении не подняты.
        service.RemoveAll<IHostedService>();

        service.RemoveAll<DirectoryServiceDbContext>();

        service.AddScoped<DirectoryServiceDbContext>(_ =>
            new DirectoryServiceDbContext(_dbContainer.GetConnectionString()));
    });

    private async Task InitializeRespawner()
    {
        // The model sets HasDefaultSchema("directory"), so every table lives there -
        // resetting "public" finds nothing and Respawn refuses to initialize.
        _respawner = await Respawner.CreateAsync(
            _dbConection,
            new RespawnerOptions { DbAdapter = DbAdapter.Postgres, SchemasToInclude = ["directory"] });
    }
}