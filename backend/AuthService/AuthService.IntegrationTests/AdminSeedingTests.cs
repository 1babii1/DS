using AuthService.Domain;
using AuthService.Web.Configuration;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace AuthService.IntegrationTests;

// The administrator the platform starts with (OpenIddictSeeder). Creating it can be refused (a password the validators do not accept, among them the
// breached-password check, which refused the committed development default on a fresh database); the seeder used to ignore the refusal, and a stack
// started with no administrator and nothing in any log. Now the outcome says which of the things happened, and the reasons, not the password.
public class AdminSeedingTests : IClassFixture<AuthTestWebFactory>, IAsyncLifetime
{
    private readonly AuthTestWebFactory _factory;
    private readonly Func<Task> _resetDatabase;

    public AdminSeedingTests(AuthTestWebFactory factory)
    {
        _factory = factory;
        _resetDatabase = factory.ResetDatabaseAsync;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _resetDatabase();

    [Fact]
    public async Task A_password_the_platform_refuses_is_reported_with_its_reasons_and_creates_nothing()
    {
        var email = $"seed-{Guid.NewGuid():N}@test.local";

        var result = await Ensure(email, "abc");

        Assert.Equal(AdminSeedStatus.Refused, result.Status);
        Assert.Contains("PasswordTooShort", result.Reasons);
        Assert.DoesNotContain("abc", string.Join(' ', result.Reasons));
        Assert.Null(await Find(email));
    }

    [Fact]
    public async Task A_password_it_accepts_creates_an_administrator_once()
    {
        var email = $"seed-{Guid.NewGuid():N}@test.local";
        var password = "Zq" + Guid.NewGuid().ToString("N") + "!9";

        var first = await Ensure(email, password);
        var second = await Ensure(email, password);

        Assert.Equal(AdminSeedStatus.Created, first.Status);
        Assert.Equal(AdminSeedStatus.AlreadyThere, second.Status);
        await using var scope = _factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
        var admin = await users.FindByEmailAsync(email);
        Assert.NotNull(admin);
        Assert.True(admin.EmailConfirmed);
        Assert.True(await users.IsInRoleAsync(admin, RoleNames.Admin));
    }

    private async Task<AdminSeedResult> Ensure(string email, string password)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await OpenIddictSeeder.EnsureAdminAsync(scope.ServiceProvider.GetRequiredService<UserManager<Account>>(), email, password);
    }

    private async Task<Account?> Find(string email)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<UserManager<Account>>().FindByEmailAsync(email);
    }
}
