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
    public static async Task SeedAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var provider = scope.ServiceProvider;

        await SeedRolesAsync(provider);
        await SeedClientApplicationAsync(provider);
        await SeedAdminUserAsync(provider);
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
