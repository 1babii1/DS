using System.Data.Common;
using AuthService.Infrastructure.Postgres;
using AuthService.IntegrationTests.Infrastructure;
using AuthService.Web.Configuration;
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
    private readonly PostgreSqlContainer _dbContainer = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("auth_service_db")
        .WithPassword("postgres")
        .WithUsername("postgres")
        .Build();

    private Respawner _respawner = null!;
    private DbConnection _dbConnection = null!;

    protected virtual int RateLimitPermits => 1000;

    public FakeEmailSender EmailSender { get; } = new();

    public FakePasswordBreachChecker PasswordBreachChecker { get; } = new();

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

        // The whole suite runs with at-rest key encryption ON (a fixed, obviously-test-only master
        // key), so every token flow exercised here also proves encrypted keys load and validate.
        builder.UseSetting(
            "SigningKeys:AtRestKeyBase64",
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("test-only-32-byte-master-key!!!!")));

        builder.ConfigureTestServices(services =>
        {
            // EmployeeEventsConsumer and the outbox publisher are both IHostedService.
            // These tests construct the consumer themselves and call
            // HandleWithRetryAndDeadLetter directly, so the host's own copies add nothing -
            // they just spend the whole run retrying against a Kafka broker that does not
            // exist here, which is what made this the one suite that failed under load.
            // Every other service's factory already removes them.
            services.RemoveAll<IHostedService>();

            services.RemoveAll<DbContextOptions<AuthDbContext>>();
            services.RemoveAll<AuthDbContext>();
            services.AddDbContext<AuthDbContext>(options => options.UseNpgsql(_dbContainer.GetConnectionString()));

            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(EmailSender);

            services.RemoveAll<IPasswordBreachChecker>();
            services.AddSingleton<IPasswordBreachChecker>(PasswordBreachChecker);
        });
    }

    private async Task InitializeRespawner()
    {
        // Roles and everything OpenIddictSeeder creates once at startup (the "roles" API
        // scope with its Resources, and the "portfolio-frontend" client application) are
        // shared setup, not per-test state - excluded from the reset so every test doesn't
        // have to re-seed them. This previously listed only the Identity-side tables
        // ("roles"/"role_claims"); the OpenIddict tables kept their PascalCase EF Core
        // defaults rather than this project's own snake_case naming, and being wiped after
        // the first test in a class silently left every later test in that class with no
        // seeded scope/application - found because a test needed the seeded client to
        // still exist for a second time in the same run. OpenIddictAuthorizations/
        // OpenIddictTokens are deliberately NOT here: those are genuinely per-test state.
        _respawner = await Respawner.CreateAsync(
            _dbConnection,
            new RespawnerOptions
            {
                DbAdapter = DbAdapter.Postgres,
                SchemasToInclude = ["auth"],
                TablesToIgnore =
                [
                    "roles", "role_claims", "__efmigrationshistory",
                    "OpenIddictApplications", "OpenIddictScopes",

                    // SigningKeySeeder seeds these once per factory lifetime, same shared-setup
                    // reasoning as the two rows above - not per-test state. Missing this was a
                    // real, observed bug: Respawn wiping signing_keys after the first test in a
                    // class left every later test with an empty table, and the next cache-miss
                    // resolution of OpenIddictServerOptions threw "At least one encryption key
                    // must be registered" instead of finding the keys seeded at startup.
                    "signing_keys",
                ],
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