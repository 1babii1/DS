using System.Text.Json;
using AuthService.Domain;
using AuthService.Infrastructure.Postgres;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

var request = await JsonSerializer.DeserializeAsync<FixtureRequest>(Console.OpenStandardInput())
    ?? throw new InvalidOperationException("A fixture request is required.");
var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
    ?? throw new InvalidOperationException("DATABASE_URL is required by the local fixture runner.");

FixtureGuard.Validate(request, connectionString, Environment.GetEnvironmentVariable("E2E_AUTH_FIXTURES"));

var services = new ServiceCollection();
services.AddDbContext<AuthDbContext>(options => options.UseNpgsql(connectionString));
services.AddIdentityCore<Account>()
    .AddRoles<Role>()
    .AddEntityFrameworkStores<AuthDbContext>();
await using var provider = services.BuildServiceProvider();
using var scope = provider.CreateScope();
var users = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();

if (request.Operation == "create")
{
    var account = new Account { UserName = request.Email, Email = request.Email, EmailConfirmed = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
    var created = await users.CreateAsync(account, request.Password!);
    if (!created.Succeeded) throw new InvalidOperationException("Fixture account could not be created.");
    var role = await users.AddToRoleAsync(account, request.Role!);
    if (!role.Succeeded) { await users.DeleteAsync(account); throw new InvalidOperationException("Fixture role could not be assigned."); }
    await Console.Out.WriteAsync(JsonSerializer.Serialize(new { id = account.Id }));
}
else
{
    var account = await users.FindByEmailAsync(request.Email);
    if (account is not null) await users.DeleteAsync(account);
}

internal sealed record FixtureRequest(string Operation, string Email, string? Password, string? Role);

internal static class FixtureGuard
{
    public static void Validate(FixtureRequest request, string connectionString, string? enabled)
    {
        if (enabled != "1") throw new InvalidOperationException("Browser fixtures require E2E_AUTH_FIXTURES=1.");
        var database = new NpgsqlConnectionStringBuilder(connectionString);
        if (database.Host is not ("localhost" or "127.0.0.1" or "::1")) throw new InvalidOperationException("Browser fixtures only run against a loopback database.");
        if (!request.Email.StartsWith("e2e-", StringComparison.Ordinal) || !request.Email.EndsWith("@test.local", StringComparison.Ordinal)) throw new InvalidOperationException("Fixture email is outside the test namespace.");
        if (request.Operation == "create" && (string.IsNullOrWhiteSpace(request.Password) || request.Role is not (RoleNames.Viewer or RoleNames.Editor))) throw new InvalidOperationException("Fixture request is invalid.");
        if (request.Operation is not ("create" or "dispose")) throw new InvalidOperationException("Fixture operation is invalid.");
    }
}
