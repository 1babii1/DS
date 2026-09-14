using System.Data.Common;
using AuthService.Infrastructure.Postgres;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Respawn;
using Testcontainers.PostgreSql;

namespace AuthService.IntegrationTests;

/// <summary>
/// Rate limiting is set generous here (not off - a real threshold, just one no
/// functional test can plausibly reach) rather than disabled outright, because
/// TestServer's in-memory transport never populates RemoteIpAddress: every request
/// in a test run would otherwise land in the same "unknown" partition and the fifth
/// register/login call in the whole fixture - regardless of which test made it -
/// would 429. <see cref="RateLimitedAuthTestWebFactory"/> is the one that actually
/// exercises the limiter, with its own low threshold and its own isolated host.
/// </summary>
public class AuthTestWebFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _dbContainer = new PostgreSqlBuilder()
        .WithImage("postgres:16")
        .WithDatabase("auth_service_db")
        .WithPassword("postgres")
        .WithUsername("postgres")
        .Build();

    private Respawner _respawner = null!;
    private DbConnection _dbConnection = null!;

    protected virtual int RateLimitPermits => 1000;

    public async Task InitializeAsync()
    {
        await _dbContainer.StartAsync();

        _dbConnection = new NpgsqlConnection(_dbContainer.GetConnectionString());
        await _dbConnection.OpenAsync();

        // Program.cs calls OpenIddictSeeder.SeedAsync(app.Services) after Build(), and
        // WebApplicationFactory runs that whole sequence the first time anything touches
        // Services (CreateClient, a scope, and so on) - not on some later, controllable
        // step. The schema has to exist before that first touch, or the seeder's own
        // RoleManager query fails on a table that isn't there yet. Building this
        // AuthDbContext by hand, independent of the factory's own DI container,
        // sidesteps ever triggering that first touch prematurely.
        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseNpgsql(_dbContainer.GetConnectionString())
            .Options;
        await using (var dbContext = new AuthDbContext(options))
        {
            await dbContext.Database.EnsureCreatedAsync();
        }

        // With the schema in place, Program.cs's own OpenIddictSeeder now runs
        // successfully on first access to Services below - roles, the OIDC client
        // application and the seed admin all come from there, not duplicated here.
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

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("RateLimiting:Auth:PermitLimit", RateLimitPermits.ToString());
        builder.UseSetting("RateLimiting:Auth:WindowSeconds", "60");

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<AuthDbContext>>();
            services.RemoveAll<AuthDbContext>();
            services.AddDbContext<AuthDbContext>(options => options.UseNpgsql(_dbContainer.GetConnectionString()));
        });
    }

    private async Task InitializeRespawner()
    {
        // Roles and the OpenIddict client application are shared setup, not per-test
        // state - excluded from the reset so every test doesn't have to re-seed them.
        _respawner = await Respawner.CreateAsync(
            _dbConnection,
            new RespawnerOptions
            {
                DbAdapter = DbAdapter.Postgres,
                SchemasToInclude = ["auth"],
                TablesToIgnore = ["roles", "role_claims", "__efmigrationshistory"],
            });
    }
}

/// <summary>
/// Isolated host with a small, real rate limit - the one factory in this project
/// meant to actually trip the limiter, rather than avoid it.
/// </summary>
public sealed class RateLimitedAuthTestWebFactory : AuthTestWebFactory
{
    protected override int RateLimitPermits => 3;
}
