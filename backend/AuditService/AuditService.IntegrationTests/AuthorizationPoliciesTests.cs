using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace AuditService.IntegrationTests;

// Controllers are called directly in these tests, so an [Authorize] that names a policy the application never registered is invisible to
// them: the endpoint then throws at request time for every caller. This asks the application's own authorization setup for every policy its controllers name.
public class AuthorizationPoliciesTests : IClassFixture<AuditTestWebFactory>
{
    private readonly IServiceProvider _services;

    public AuthorizationPoliciesTests(AuditTestWebFactory factory) => _services = factory.Services;

    [Fact]
    public async Task Every_policy_a_controller_asks_for_is_registered()
    {
        var provider = _services.GetRequiredService<IAuthorizationPolicyProvider>();
        var named = typeof(Web.Controllers.AuditController).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t))
            .SelectMany(t => t.GetCustomAttributes<AuthorizeAttribute>(true)
                .Concat(t.GetMethods().SelectMany(m => m.GetCustomAttributes<AuthorizeAttribute>(true))))
            .Select(a => a.Policy)
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct()
            .ToList();

        Assert.Contains("StepUp", named);
        Assert.Contains("IsAdmin", named);
        foreach (var policy in named)
        {
            Assert.True(await provider.GetPolicyAsync(policy!) is not null, $"policy '{policy}' is asked for by a controller and never registered");
        }
    }
}
