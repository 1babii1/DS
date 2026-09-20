using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shared.Security;

namespace Shared.UnitTests;

// The point of moving Auth config onto the options pattern was to turn a silent null into
// a loud startup failure. That guarantee is only real if validation actually rejects a
// missing value, so it is asserted here rather than assumed from the presence of
// ValidateDataAnnotations.
public class JwtAuthOptionsTests
{
    private const string Metadata = "http://auth/.well-known/openid-configuration";

    [Fact]
    public void A_complete_section_is_accepted_and_reaches_the_bearer_handler()
    {
        var provider = Build(new Dictionary<string, string?>
        {
            ["Auth:MetadataAddress"] = Metadata,
            ["Auth:Issuer"] = "http://auth",
            ["Auth:Audience"] = "audit-service",
        });

        var jwt = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

        Assert.Equal(Metadata, jwt.MetadataAddress);
        Assert.Equal("http://auth", jwt.TokenValidationParameters.ValidIssuer);
        Assert.Equal("audit-service", jwt.TokenValidationParameters.ValidAudience);

        // Claim mapping is what every controller's User.IsInRole and "sub" lookups rely on.
        Assert.False(jwt.MapInboundClaims);
        Assert.Equal("role", jwt.TokenValidationParameters.RoleClaimType);
        Assert.Equal("name", jwt.TokenValidationParameters.NameClaimType);
    }

    [Theory]
    [InlineData("Auth:MetadataAddress")]
    [InlineData("Auth:Issuer")]
    [InlineData("Auth:Audience")]
    public void A_missing_value_is_rejected_rather_than_bound_as_null(string omitted)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Auth:MetadataAddress"] = Metadata,
            ["Auth:Issuer"] = "http://auth",
            ["Auth:Audience"] = "audit-service",
        };
        settings.Remove(omitted);

        var provider = Build(settings);

        var failure = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<JwtAuthOptions>>().Value);

        Assert.Contains(omitted.Split(':')[1], string.Join(" ", failure.Failures));
    }

    [Fact]
    public void An_empty_value_counts_as_missing()
    {
        var provider = Build(new Dictionary<string, string?>
        {
            ["Auth:MetadataAddress"] = string.Empty,
            ["Auth:Issuer"] = "http://auth",
            ["Auth:Audience"] = "audit-service",
        });

        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<JwtAuthOptions>>().Value);
    }

    [Fact]
    public void An_absent_section_entirely_is_rejected()
    {
        var provider = Build([]);

        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<JwtAuthOptions>>().Value);
    }

    [Fact]
    public void Https_metadata_is_required_in_production_and_relaxed_elsewhere()
    {
        Dictionary<string, string?> Settings(string metadataAddress) => new()
        {
            ["Auth:MetadataAddress"] = metadataAddress,
            ["Auth:Issuer"] = "http://auth",
            ["Auth:Audience"] = "audit-service",
        };

        Assert.True(
            BearerFor(Settings("https://auth/.well-known/openid-configuration"), Environments.Production)
                .RequireHttpsMetadata);

        // Plain http is what the local/Docker stack actually serves, and it has to keep working.
        Assert.False(BearerFor(Settings(Metadata), Environments.Development).RequireHttpsMetadata);
    }

    [Fact]
    public void Production_refuses_a_plain_http_metadata_address()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Auth:MetadataAddress"] = Metadata,
            ["Auth:Issuer"] = "http://auth",
            ["Auth:Audience"] = "audit-service",
        };

        // Enforced by JwtBearer's own post-configure step, and worth pinning: it is the
        // reason RequireHttpsMetadata is tied to the environment rather than hardcoded.
        Assert.Throws<InvalidOperationException>(() => BearerFor(settings, Environments.Production));
    }

    private static JwtBearerOptions BearerFor(Dictionary<string, string?> settings, string environment) =>
        Build(settings, environment)
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

    private static ServiceProvider Build(
        Dictionary<string, string?> settings, string? environment = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        return new ServiceCollection()
            .AddLogging()
            .AddPlatformJwtAuthentication(
                configuration, new StubEnvironment(environment ?? Environments.Development))
            .BuildServiceProvider();
    }

    private sealed class StubEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
