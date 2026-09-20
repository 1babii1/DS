using System.Data.Common;
using DirectoryService.Infrastructure.Postgres;
using Microsoft.AspNetCore.Authentication;
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
    private readonly PostgreSqlContainer _dbContainer = new PostgreSqlBuilder("pgvector/pgvector:pg18")
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

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // NpgsqlConnectionFactory (every Dapper-based read handler, e.g.
        // GetChildrenLazyHandler) reads ConnectionStrings:DirectoryServiceDb straight
        // from IConfiguration at construction - overriding only the DbContext
        // registration below leaves it pointed at whatever real database appsettings
        // configures, not this test's isolated container. Without this, every Dapper
        // read in the test suite silently queries the wrong database.
        //
        // Search Path matters here too: the model declares HasDefaultSchema("directory"),
        // so EF always schema-qualifies its own generated SQL regardless of search_path,
        // but the raw Dapper queries (GetChildrenLazyFromDb and friends) do not - they
        // rely on the connection's search_path to resolve "departments", same as the
        // real appsettings connection strings already do.
        builder.UseSetting(
            "ConnectionStrings:DirectoryServiceDb",
            _dbContainer.GetConnectionString() + ";Search Path=directory,public");

        // TestServer never populates RemoteIpAddress, so every request in the whole
        // suite shares one rate-limit partition - the real default (30/min) would
        // trip well before this suite's write-heavy tests finish. Same reasoning as
        // AuthTestWebFactory's RateLimitPermits.
        builder.UseSetting("RateLimiting:Write:PermitLimit", "1000");
        builder.UseSetting("RateLimiting:Write:WindowSeconds", "60");

        builder.ConfigureTestServices(service =>
        {
            // Тесты вызывают хендлеры напрямую, фоновые сервисы в них не участвуют, но
            // мешают: воркер ретеншена удаляет из тех же таблиц, что чистит Respawn между
            // тестами, и два конкурирующих DELETE ловят deadlock, из-за чего падает
            // случайный тест. Воркеры эмбеддингов и outbox к тому же непрерывно логируют
            // ошибки, потому что Ollama и Kafka в тестовом окружении не подняты.
            service.RemoveAll<IHostedService>();

            // Program.cs wires JWT Bearer against a real AuthService JWKS endpoint this
            // test host doesn't have. Query-contract tests need [Authorize] to actually
            // run (not a real credential check) to prove the HTTP-level status/body a
            // fix like this one changes, so the default scheme becomes a fixed
            // authenticated test principal instead.
            service
                .AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            service.RemoveAll<DirectoryServiceDbContext>();

            service.AddScoped<DirectoryServiceDbContext>(_ =>
                new DirectoryServiceDbContext(_dbContainer.GetConnectionString()));
        });
    }

    private async Task InitializeRespawner()
    {
        // The model sets HasDefaultSchema("directory"), so every table lives there -
        // resetting "public" finds nothing and Respawn refuses to initialize.
        _respawner = await Respawner.CreateAsync(
            _dbConection,
            new RespawnerOptions { DbAdapter = DbAdapter.Postgres, SchemasToInclude = ["directory"] });
    }
}