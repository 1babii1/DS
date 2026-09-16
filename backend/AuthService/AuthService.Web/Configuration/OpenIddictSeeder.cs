using AuthService.Application;
using AuthService.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AuthService.Web.Configuration;

// Assumes migrations were already applied by the dedicated auth_service_migrations
// container (see docker-compose.yml) - running EF migrations inline at every app
// startup under `restart: always` is fragile: a transient DB hiccup mid-migration
// triggers a restart that then collides with tables the previous attempt already created.
public static class OpenIddictSeeder
{
    // The client always requests this scope alongside openid/email/profile (see
    // AuthTestWebFactory and load-tests/k6/lib/auth.js) - reusing it to carry the
    // resource list means every existing login already gets the resulting audience
    // claim, with no client-side change required to request a new scope.
    private const string ApiScopeName = "roles";

    // Every JWT-bearer resource server's own ValidAudience/ValidAudiences - one shared
    // token can call any of them, matching this app's single-SPA-session design; a
    // resource server outside this list (or a token from a different issuer entirely)
    // is correctly rejected once ValidateAudience is on.
    private static readonly string[] ApiResources =
    [
        "directory-service",
        "employee-service",
        "audit-service",
        "mcp-server",
        "rewards-service",
    ];

    public static async Task SeedAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var provider = scope.ServiceProvider;

        await SeedRolesAsync(provider);
        await SeedApiScopeAsync(provider);
        await SeedClientApplicationAsync(provider);
        await SeedAdminUserAsync(provider);
    }

    // Without a scope descriptor carrying a Resources list, OpenIddict issues tokens
    // with no aud claim at all - ValidateAudience=false everywhere was the only way
    // those tokens could be accepted. This is what makes ValidateAudience=true safe.
    private static async Task SeedApiScopeAsync(IServiceProvider provider)
    {
        var scopeManager = provider.GetRequiredService<IOpenIddictScopeManager>();

        if (await scopeManager.FindByNameAsync(ApiScopeName) is not null)
        {
            return;
        }

        var descriptor = new OpenIddictScopeDescriptor { Name = ApiScopeName };
        foreach (var resource in ApiResources)
        {
            descriptor.Resources.Add(resource);
        }

        await scopeManager.CreateAsync(descriptor);
    }

    private static async Task SeedRolesAsync(IServiceProvider provider)
    {
        var roleManager = provider.GetRequiredService<RoleManager<Role>>();

        foreach (var roleName in RoleNames.All)
        {
            if (!await roleManager.RoleExistsAsync(roleName))
            {
                await roleManager.CreateAsync(new Role(roleName));
            }
        }
    }

    private static async Task SeedClientApplicationAsync(IServiceProvider provider)
    {
        var applicationManager = provider.GetRequiredService<IOpenIddictApplicationManager>();

        if (await applicationManager.FindByClientIdAsync("portfolio-frontend") is not null)
        {
            return;
        }

        await applicationManager.CreateAsync(new OpenIddictApplicationDescriptor
        {
            ClientId = "portfolio-frontend",
            ClientType = ClientTypes.Public,
            ConsentType = ConsentTypes.Implicit,
            DisplayName = "Portfolio frontend",
            RedirectUris = { new Uri("http://localhost:3000/auth/callback") },
            PostLogoutRedirectUris = { new Uri("http://localhost:3000") },
            Permissions =
            {
                Permissions.Endpoints.Authorization,
                Permissions.Endpoints.Token,
                Permissions.Endpoints.EndSession,
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.GrantTypes.RefreshToken,
                Permissions.ResponseTypes.Code,
                Permissions.Scopes.Email,
                Permissions.Scopes.Profile,
                Permissions.Scopes.Roles,
                Permissions.Prefixes.Scope + "offline_access",
            },
            Requirements = { Requirements.Features.ProofKeyForCodeExchange },
        });
    }

    private static async Task SeedAdminUserAsync(IServiceProvider provider)
    {
        var options = provider.GetRequiredService<IOptions<AuthOptions>>().Value.SeedAdmin;
        var userManager = provider.GetRequiredService<UserManager<Account>>();

        if (await userManager.FindByEmailAsync(options.Email) is not null)
        {
            return;
        }

        var admin = new Account
        {
            UserName = options.Email,
            Email = options.Email,
            EmailConfirmed = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        var result = await userManager.CreateAsync(admin, options.Password);
        if (result.Succeeded)
        {
            await userManager.AddToRoleAsync(admin, RoleNames.Admin);
        }
    }
}