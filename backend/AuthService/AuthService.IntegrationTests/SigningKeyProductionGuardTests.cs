using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using AuthService.Web.Configuration;

namespace AuthService.IntegrationTests;

// The rule that keeps plaintext private keys out of production: without the at-rest master key
// the host must refuse to start in the Production environment, and an explicitly configured key
// must not trip the same guard.
public class SigningKeyProductionGuardTests : IClassFixture<AuthTestWebFactory>
{
    // Production has no appsettings connection string of its own; the guard under test fires
    // during service registration, before anything would try to connect.
    private const string UnreachableDb = "Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=1";

    private readonly AuthTestWebFactory _factory;

    public SigningKeyProductionGuardTests(AuthTestWebFactory factory) => _factory = factory;

    [Fact]
    public void Production_refuses_to_start_without_the_at_rest_master_key()
    {
        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("ConnectionStrings:AuthServiceDb", UnreachableDb);
            builder.UseSetting(SigningKeyProtector.ConfigurationKey, string.Empty);
        });

        var exception = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(exception);
        Assert.Contains(SigningKeyProtector.ConfigurationKey, Flatten(exception), StringComparison.Ordinal);
    }

    [Fact]
    public void Production_does_not_trip_the_guard_when_the_key_is_configured()
    {
        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("ConnectionStrings:AuthServiceDb", UnreachableDb);
        });

        var exception = Record.Exception(() => factory.CreateClient());

        // Production may still fail to start for OTHER reasons in a test host (its own strict
        // options); what this asserts is only that this guard is not the one complaining.
        Assert.DoesNotContain(SigningKeyProtector.ConfigurationKey, exception is null ? string.Empty : Flatten(exception), StringComparison.Ordinal);
    }

    private static string Flatten(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            messages.Add(current.Message);
            if (current.InnerException is null)
            {
                break;
            }
        }

        return string.Join(" | ", messages);
    }
}
