using AuthService.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace AuthService.Web.Configuration;

/// <summary>
/// Marks an endpoint as admin-only. Unlike Shared's CanEdit (admin or editor, reused by
/// three resource services), this policy is specific to AuthService's own account-management
/// surface and has exactly one consumer today, so it stays local rather than moving to
/// Shared for a reuse that does not exist yet.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class RequireAdminAttribute : AuthorizeAttribute
{
    public const string PolicyName = "AdminOnly";

    public RequireAdminAttribute()
        : base(PolicyName)
    {
    }
}

public static class AdminAuthorizationExtensions
{
    public static IServiceCollection AddAdminPolicy(this IServiceCollection services)
    {
        services.AddAuthorizationBuilder()
            .AddPolicy(RequireAdminAttribute.PolicyName, policy => policy.RequireRole(RoleNames.Admin));

        return services;
    }
}
