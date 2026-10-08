using AuthService.Application;
using AuthService.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
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
        "notification-service",
        "search-service",
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
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(OpenIddictSeeder));

        var result = await EnsureAdminAsync(provider.GetRequiredService<UserManager<Account>>(), options.Email, options.Password);
        switch (result.Status)
        {
            case AdminSeedStatus.Created:
                logger.LogInformation("The seeded administrator {Email} was created", options.Email);
                break;
            case AdminSeedStatus.Refused:
                // Said out loud, because the alternative was a stack that starts, looks healthy and has nobody who can sign in (the password
                // never appears here, only what the validators called wrong with it).
                logger.LogError(
                    "The seeded administrator {Email} could not be created, so nobody can sign in as one: {Reasons}. Set Auth:SeedAdmin:Password to a password the platform accepts",
                    options.Email,
                    string.Join(", ", result.Reasons));
                break;
            case AdminSeedStatus.RoleNotGranted:
                logger.LogError(
                    "The seeded administrator {Email} was created but could not be given the administrator role: {Reasons}",
                    options.Email,
                    string.Join(", ", result.Reasons));
                break;
        }
    }

    /// <summary>Creates the administrator the platform starts with, once. The outcome says which of the three things happened; it never says the password.</summary>
    public static async Task<AdminSeedResult> EnsureAdminAsync(UserManager<Account> userManager, string email, string password)
    {
        if (await userManager.FindByEmailAsync(email) is not null)
        {
            return new AdminSeedResult(AdminSeedStatus.AlreadyThere, []);
        }

        var admin = new Account
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        var created = await userManager.CreateAsync(admin, password);
        if (!created.Succeeded)
        {
            return new AdminSeedResult(AdminSeedStatus.Refused, created.Errors.Select(e => e.Code).ToList());
        }

        var granted = await userManager.AddToRoleAsync(admin, RoleNames.Admin);
        return granted.Succeeded
            ? new AdminSeedResult(AdminSeedStatus.Created, [])
            : new AdminSeedResult(AdminSeedStatus.RoleNotGranted, granted.Errors.Select(e => e.Code).ToList());
    }
}

public enum AdminSeedStatus
{
    Created,
    AlreadyThere,
    Refused,
    RoleNotGranted,
}

public sealed record AdminSeedResult(AdminSeedStatus Status, IReadOnlyList<string> Reasons);
