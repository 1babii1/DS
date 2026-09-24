using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Shared.Security;

namespace Shared.Ops;

public static class OpsPolicy
{
    public const string Name = "OpsAdmin";

    // Admin only, not CanEdit: these endpoints expose raw event payloads (which can carry
    // emails and other personal data) and let the caller re-inject events, so the bar is the
    // admin role, plus step-up for anything that changes state (see OpsEndpointExtensions).
    public static IServiceCollection AddOpsPolicy(this IServiceCollection services)
    {
        services.AddAuthorizationBuilder()
            .AddPolicy(Name, policy => policy.RequireRole(RoleNames.Admin));
        return services;
    }
}
