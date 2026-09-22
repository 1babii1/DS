using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Shared.Security;

/// <summary>
/// The claims contract for GitHub-style "sudo mode": AuthService's AuthorizationController
/// stamps <see cref="ElevatedUntilClaim"/> onto a refreshed access token after the user
/// re-verifies via StepUpService, and this policy is what a resource-service endpoint
/// actually gates on. No callback to AuthService is involved - every service already
/// validates access tokens locally against the same signing key (JwtAuthenticationExtensions),
/// so this claim rides along on that same trust chain instead of inventing a second one.
/// </summary>
public static class StepUpClaims
{
    /// <summary>Unix seconds (string, matching how OpenIddict/System.IdentityModel emit
    /// numeric claims) until which the token's subject is considered freshly re-verified.</summary>
    public const string ElevatedUntilClaim = "elevated_until";
}

public class StepUpRequirement : IAuthorizationRequirement;

/// <summary>Succeeds only if the token carries an elevated_until claim whose value is still
/// in the future - presence alone is not enough, since the access token itself can outlive
/// the elevation window it was minted with.</summary>
public class StepUpAuthorizationHandler : AuthorizationHandler<StepUpRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, StepUpRequirement requirement)
    {
        var claim = context.User.FindFirst(StepUpClaims.ElevatedUntilClaim)?.Value;
        if (claim is not null
            && long.TryParse(claim, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            && DateTimeOffset.FromUnixTimeSeconds(seconds) > DateTimeOffset.UtcNow)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Marks an endpoint as requiring a fresh GitHub-style "sudo mode" re-verification, on top
/// of whatever normal authorization it already has. Reads as intent ("this needs a recent
/// confirmation") instead of a bare policy-name string an editor can't check, and multiple
/// [Authorize]-family attributes on one action combine with AND semantics, so this adds to
/// an existing [Authorize(Policy = "CanEdit")] rather than replacing it.
/// </summary>
/// <example><c>[HttpDelete] [Authorize(Policy = "CanEdit")] [RequireStepUp] public async
/// Task Terminate(...)</c></example>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class RequireStepUpAttribute : AuthorizeAttribute
{
    public RequireStepUpAttribute()
        : base(StepUpAuthorizationExtensions.PolicyName)
    {
    }
}

public static class StepUpAuthorizationExtensions
{
    public const string PolicyName = "StepUp";

    /// <summary>Registers the "StepUp" policy - apply it to a sensitive endpoint with
    /// <c>[RequireStepUp]</c> alongside its normal authorization, the same one-line shape
    /// as AddPlatformJwtAuthentication.</summary>
    public static IServiceCollection AddStepUpPolicy(this IServiceCollection services)
    {
        services.AddSingleton<IAuthorizationHandler, StepUpAuthorizationHandler>();
        services.AddAuthorizationBuilder()
            .AddPolicy(PolicyName, policy => policy.Requirements.Add(new StepUpRequirement()));

        return services;
    }
}
