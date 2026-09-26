using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AuthService.Web.Configuration;

public static class WebClientSeeder
{
    public static async Task SeedAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<WebClientOptions>>().Value;
        if (!options.Enabled)
        {
            return;
        }

        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var origin = new Uri(options.FrontendOrigin);
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = WebClientOptions.ClientId,
            ClientSecret = options.ClientSecret,
            ClientType = ClientTypes.Confidential,
            ConsentType = ConsentTypes.Implicit,
            DisplayName = "DS web application",
            RedirectUris = { new Uri(origin, "/api/auth/callback/openiddict") },
            PostLogoutRedirectUris = { new Uri(origin, "/login") },
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
                Permissions.Prefixes.Scope + Scopes.OfflineAccess,
            },
            Requirements = { Requirements.Features.ProofKeyForCodeExchange },
        };

        var application = await manager.FindByClientIdAsync(WebClientOptions.ClientId);
        if (application is null)
        {
            await manager.CreateAsync(descriptor);
        }
        else
        {
            await manager.UpdateAsync(application, descriptor);
        }
    }
}
