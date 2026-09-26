using System.Data.Common;
using Elastic.Clients.Elasticsearch;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Respawn;
using SearchService.Infrastructure.Elasticsearch;
using SearchService.Infrastructure.Postgres;
using SearchService.Web;
using Testcontainers.Elasticsearch;
using Testcontainers.PostgreSql;

namespace SearchService.IntegrationTests;

public class SearchTestWebFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _dbContainer = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("search_service_db")
        .WithPassword("postgres")
        .WithUsername("postgres")
        .Build();

    // Security/TLS disabled the same way docker-compose's own elasticsearch service is
    // (dev-appropriate posture, see the search ADR) - the module's default enables a
    // self-signed HTTPS cert + basic auth, which this test client has no reason to trust.
    private readonly ElasticsearchContainer _elasticsearchContainer =
        new ElasticsearchBuilder("docker.elastic.co/elasticsearch/elasticsearch:8.15.0")
            .WithEnvironment("xpack.security.enabled", "false")
            .WithEnvironment("xpack.security.http.ssl.enabled", "false")
            .Build();

    private Respawner _respawner = null!;
    private DbConnection _dbConnection = null!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_dbContainer.StartAsync(), _elasticsearchContainer.StartAsync());

        _dbConnection = new NpgsqlConnection(_dbContainer.GetConnectionString());
        await _dbConnection.OpenAsync();

        await using var scope = Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SearchDbContext>();
        await dbContext.Database.EnsureCreatedAsync();

        var indexClient = scope.ServiceProvider.GetRequiredService<SearchIndexClient>();
        await indexClient.EnsureIndexAsync(CancellationToken.None);

        await InitializeRespawner();
    }

    public new async Task DisposeAsync()
    {
        if (_dbConnection is not null)
        {
            await _dbConnection.DisposeAsync();
        }

        await _dbContainer.DisposeAsync();
        await _elasticsearchContainer.DisposeAsync();
    }

    public async Task ResetDatabaseAsync() => await _respawner.ResetAsync(_dbConnection);

    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureTestServices(services =>
    {
        // DomainEventsConsumer/OutboxPublisher are IHostedServices - removing them means
        // tests construct and call the consumer directly instead of racing against the
        // host's own auto-started instance, which would try to connect to a broker that
        // doesn't exist in this test run.
        services.RemoveAll<IHostedService>();

        services.RemoveAll<DbContextOptions<SearchDbContext>>();
        services.RemoveAll<SearchDbContext>();
        services.AddDbContext<SearchDbContext>(options => options.UseNpgsql(_dbContainer.GetConnectionString()));

        services.RemoveAll<ElasticsearchClient>();
        var settings = new ElasticsearchClientSettings(new Uri(_elasticsearchContainer.GetConnectionString()))
            .DefaultIndex("search-entries");
        services.AddSingleton(new ElasticsearchClient(settings));
    });

    private async Task InitializeRespawner()
    {
        _respawner = await Respawner.CreateAsync(
            _dbConnection,
            new RespawnerOptions { DbAdapter = DbAdapter.Postgres, SchemasToInclude = ["search"] });
    }
}
