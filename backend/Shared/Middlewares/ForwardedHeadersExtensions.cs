using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

// Both namespaces define IPNetwork. The alias pins the framework type explicitly -
// Microsoft.AspNetCore.HttpOverrides.IPNetwork is deprecated in favour of this one.
using IPNetwork = System.Net.IPNetwork;

namespace Shared.Middlewares;

/// <summary>
/// Restores the real client IP from nginx's X-Forwarded-For.
/// <para>
/// Every service behind nginx sees the nginx container's IP as
/// <c>HttpContext.Connection.RemoteIpAddress</c>. The rate limiters partition on exactly
/// that value, so without this every caller in the system shares a single rate-limit
/// bucket: the per-IP throttle stops limiting an attacker and instead lets one client
/// exhaust the budget for everyone.
/// </para>
/// <para>
/// X-Forwarded-For is client-supplied and trivially spoofed, so it is only honoured from
/// the proxy network itself - trusting it from anywhere would hand an attacker an unlimited
/// supply of fresh rate-limit partitions, which is worse than the bug being fixed.
/// </para>
/// </summary>
public static class ForwardedHeadersExtensions
{
    /// <summary>Docker's default bridge range, where nginx lives. Overridable per environment.</summary>
    private const string DefaultProxyNetwork = "172.16.0.0/12";

    public static IServiceCollection AddProxyForwardedHeaders(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var networks = configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>();
        if (networks is null || networks.Length == 0)
        {
            networks = [DefaultProxyNetwork];
        }

        return services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

            // Exactly one proxy (nginx) sits in front of these services.
            options.ForwardLimit = 1;

            // Defaults trust loopback only, which is never where nginx is.
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();

            foreach (var network in networks)
            {
                // Parse rejects a CIDR whose base address has bits set past the prefix
                // (for example 192.168.5.1/24), so a misconfigured network fails at
                // startup instead of quietly trusting something other than intended.
                options.KnownIPNetworks.Add(IPNetwork.Parse(network));
            }
        });
    }

    /// <summary>
    /// Must run before anything that reads the client IP - correlation ids, request
    /// logging, and above all the rate limiter.
    /// </summary>
    public static IApplicationBuilder UseProxyForwardedHeaders(this IApplicationBuilder app) =>
        app.UseForwardedHeaders();
}
