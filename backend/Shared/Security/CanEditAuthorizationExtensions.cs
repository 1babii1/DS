using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Shared.Security;

/// <summary>
/// Marks an endpoint as requiring the write role (admin or editor). Previously three
/// services (DirectoryService, EmployeeService, RewardsService) each spelled out an
/// identical <c>.AddPolicy("CanEdit", policy => policy.RequireRole(RoleNames.Admin,
/// RoleNames.Editor))</c> and <c>[Authorize(Policy = "CanEdit")]</c> - same policy, same
/// name, same roles, copy-pasted three times - centralized here the same way StepUp is.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class RequireCanEditAttribute : AuthorizeAttribute
{
    public RequireCanEditAttribute()
        : base(CanEditAuthorizationExtensions.PolicyName)
    {
    }
}

public static class CanEditAuthorizationExtensions
{
    public const string PolicyName = "CanEdit";

    /// <summary>Registers the "CanEdit" policy - apply it to a write endpoint with
    /// <c>[RequireCanEdit]</c>, the same one-line shape as AddPlatformJwtAuthentication
    /// and AddStepUpPolicy.</summary>
    public static IServiceCollection AddCanEditPolicy(this IServiceCollection services)
    {
        services.AddAuthorizationBuilder()
            .AddPolicy(PolicyName, policy => policy.RequireRole(RoleNames.Admin, RoleNames.Editor));

        return services;
    }
}
