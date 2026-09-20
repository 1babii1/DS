using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shared.Middlewares;

namespace Shared.UnitTests;

// The rate limiters partition on RemoteIpAddress, so what this configuration trusts
// decides whether throttling works at all. Both halves matter and are asserted here: the
// proxy's header must be honoured (otherwise every caller shares one bucket), and nobody
// else's must be (otherwise a caller mints a fresh bucket per request by spoofing it).
public class ForwardedHeadersTests
{
    [Fact]
    public void The_default_trusts_the_docker_proxy_network_and_nothing_else()
    {
        var options = Configure();

        var network = Assert.Single(options.KnownIPNetworks);
        Assert.Equal(IPAddress.Parse("172.16.0.0"), network.BaseAddress);
        Assert.Equal(12, network.PrefixLength);

        // The framework default trusts loopback; nginx is never on loopback here, and
        // leaving it in place would silently widen what is trusted.
        Assert.Empty(options.KnownProxies);
    }

    [Fact]
    public void Only_the_forwarded_for_and_proto_headers_are_honoured()
    {
        var options = Configure();

        // X-Forwarded-Host is deliberately absent: honouring a client-supplied host is how
        // host-header poisoning gets into links and redirects.
        Assert.Equal(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto, options.ForwardedHeaders);
    }

    [Fact]
    public void Exactly_one_proxy_hop_is_expected()
    {
        // With a longer limit, a caller can prepend their own entries to X-Forwarded-For
        // and choose which address the server ends up believing.
        Assert.Equal(1, Configure().ForwardLimit);
    }

    [Fact]
    public void The_trusted_network_can_be_overridden_per_environment()
    {
        var options = Configure(new Dictionary<string, string?>
        {
            ["ForwardedHeaders:KnownNetworks:0"] = "10.1.0.0/16",
            ["ForwardedHeaders:KnownNetworks:1"] = "192.168.5.0/24",
        });

        Assert.Equal(2, options.KnownIPNetworks.Count);
        Assert.Equal(IPAddress.Parse("10.1.0.0"), options.KnownIPNetworks[0].BaseAddress);
        Assert.Equal(16, options.KnownIPNetworks[0].PrefixLength);
        Assert.Equal(24, options.KnownIPNetworks[1].PrefixLength);
    }

    [Fact]
    public void An_empty_configured_list_falls_back_to_the_default_rather_than_trusting_nothing()
    {
        // Trusting nothing is not a safe default here - it silently restores the original
        // bug where every caller shares one rate-limit partition.
        var options = Configure(new Dictionary<string, string?>());

        Assert.Single(options.KnownIPNetworks);
    }

    [Theory]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.254", true)]
    [InlineData("172.15.0.1", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("8.8.8.8", false)]
    public void The_default_network_covers_dockers_range_without_spilling_outside_it(string address, bool trusted)
    {
        var network = Configure().KnownIPNetworks.Single();

        Assert.Equal(trusted, network.Contains(IPAddress.Parse(address)));
    }

    private static ForwardedHeadersOptions Configure(Dictionary<string, string?>? settings = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings ?? [])
            .Build();

        return new ServiceCollection()
            .AddProxyForwardedHeaders(configuration)
            .BuildServiceProvider()
            .GetRequiredService<IOptions<ForwardedHeadersOptions>>()
            .Value;
    }
}
