using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shared.Resilience;

namespace AuthService.IntegrationTests;

// Password hashing is the expensive work an unauthenticated caller can ask for, so the credential endpoints are a class of their own (ADR 0045):
// a flood of logins fills that compartment and not the one that serves token validation keys and the rest. This checks the shipped configuration.
public class CredentialsBulkheadTests : IClassFixture<AuthTestWebFactory>
{
    private readonly IServiceProvider _services;

    public CredentialsBulkheadTests(AuthTestWebFactory factory) => _services = factory.Services;

    [Fact]
    public void The_credential_endpoints_have_a_class_of_their_own_inside_the_overall_limit()
    {
        var options = _services.GetRequiredService<IOptions<LoadSheddingOptions>>().Value;

        var credentials = Assert.Contains("credentials", options.Classes);
        Assert.Contains("/auth/login", credentials.PathPrefixes);
        Assert.Contains("/connect/token", credentials.PathPrefixes);
        Assert.True(credentials.MaxConcurrentRequests < options.MaxConcurrentRequests);
    }

    [Fact]
    public void The_keys_other_services_validate_tokens_with_are_not_in_that_class()
    {
        var options = _services.GetRequiredService<IOptions<LoadSheddingOptions>>().Value;

        var credentials = options.Classes["credentials"];

        Assert.DoesNotContain(credentials.PathPrefixes, p => p.Contains(".well-known", StringComparison.Ordinal));
    }
}
