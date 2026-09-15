using System.Security.Cryptography;
using AuthService.Domain;
using AuthService.Infrastructure.Postgres;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Validation.AspNetCore;
using Quartz;

namespace AuthService.Web.Configuration;

public static class AuthenticationConfiguration
{
    private static bool IsLocalInsecureEnvironment(IWebHostEnvironment environment)
        => environment.IsDevelopment() || environment.IsEnvironment("Docker");

    public static IServiceCollection AddIdentityServices(
        this IServiceCollection services,
        IWebHostEnvironment environment)
    {
        services.AddIdentity<Account, Role>(options =>
            {
                options.User.RequireUniqueEmail = true;

                options.Password.RequiredLength = 8;
                options.Password.RequireUppercase = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireDigit = true;
                options.Password.RequireNonAlphanumeric = false;

                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);

                options.ClaimsIdentity.UserIdClaimType = OpenIddictConstants.Claims.Subject;
                options.ClaimsIdentity.UserNameClaimType = OpenIddictConstants.Claims.Name;
                options.ClaimsIdentity.EmailClaimType = OpenIddictConstants.Claims.Email;
                options.ClaimsIdentity.RoleClaimType = OpenIddictConstants.Claims.Role;
            })
            .AddEntityFrameworkStores<AuthDbContext>()
            .AddDefaultTokenProviders();

        // AddIdentity overrides the default scheme to Identity cookies.
        // Restore OpenIddict validation as default so API endpoints use Bearer tokens;
        // the cookie scheme is still used explicitly by the /connect/authorize endpoint.
        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme;
        });

        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = "AuthServiceCookie";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = IsLocalInsecureEnvironment(environment)
                ? CookieSecurePolicy.SameAsRequest
                : CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Events.OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        });

        return services;
    }

    public static IServiceCollection AddOpenIddictServer(
        this IServiceCollection services,
        IWebHostEnvironment environment,
        IConfiguration configuration)
    {
        services.AddOpenIddict()
            .AddCore(options =>
            {
                options.UseEntityFrameworkCore()
                    .UseDbContext<AuthDbContext>();

                // Without this, openiddict_tokens/openiddict_authorizations grow one
                // row per issued authorization code/access token/refresh token,
                // forever. Prunes expired/redundant rows on a recurring Quartz job
                // (hourly by default) - see AddOpenIddictQuartzScheduler below for the
                // scheduler itself.
                options.UseQuartz();
            })
            .AddServer(options =>
            {
                var issuer = configuration["Auth:Issuer"];
                if (!string.IsNullOrWhiteSpace(issuer))
                {
                    options.SetIssuer(new Uri(issuer));
                }

                options
                    .AllowAuthorizationCodeFlow()
                    .RequireProofKeyForCodeExchange()
                    .AllowRefreshTokenFlow()
                    .AllowClientCredentialsFlow();

                options
                    .SetAuthorizationEndpointUris("connect/authorize")
                    .SetTokenEndpointUris("connect/token")
                    .SetUserInfoEndpointUris("connect/userinfo")
                    .SetEndSessionEndpointUris("connect/logout");

                options.RegisterScopes(
                    OpenIddictConstants.Scopes.OpenId,
                    OpenIddictConstants.Scopes.Profile,
                    OpenIddictConstants.Scopes.Email,
                    OpenIddictConstants.Scopes.Roles,
                    OpenIddictConstants.Scopes.OfflineAccess);

                var aspNetCoreBuilder = options.UseAspNetCore()
                    .EnableAuthorizationEndpointPassthrough()
                    .EnableTokenEndpointPassthrough()
                    .EnableUserInfoEndpointPassthrough()
                    .EnableEndSessionEndpointPassthrough();

                if (IsLocalInsecureEnvironment(environment))
                {
                    aspNetCoreBuilder.DisableTransportSecurityRequirement();
                }

                options.SetAccessTokenLifetime(TimeSpan.FromMinutes(15));
                options.SetRefreshTokenLifetime(TimeSpan.FromDays(30));

                // Other services (DirectoryService, etc.) validate access tokens locally via JWKS,
                // not through introspection calls back to AuthService - they need plain signed JWTs.
                options.DisableAccessTokenEncryption();

                AddSigningKeys(options, environment, configuration);
            })
            .AddValidation(options =>
            {
                options.UseLocalServer();
                options.UseAspNetCore();
            });

        return services;
    }

    /// <summary>
    /// Real keys when configured (any environment - a self-hosted demo of this project
    /// is free to supply them even outside Production), a hard failure at startup if
    /// Production has none (better than a server that starts with no way to sign a
    /// token, or worse, silently falls back to a throwaway dev certificate a redeploy
    /// invalidates every existing session against), and the ephemeral OpenIddict dev
    /// certificate otherwise - the only case this was ever handling before.
    /// </summary>
    private static void AddSigningKeys(
        OpenIddictServerBuilder builder,
        IWebHostEnvironment environment,
        IConfiguration configuration)
    {
        var signingKeyBase64 = configuration["SigningKeys:SigningKeyBase64"];
        var encryptionKeyBase64 = configuration["SigningKeys:EncryptionKeyBase64"];

        if (!string.IsNullOrWhiteSpace(signingKeyBase64) && !string.IsNullOrWhiteSpace(encryptionKeyBase64))
        {
            builder.AddSigningKey(ImportRsaKey(signingKeyBase64));
            builder.AddEncryptionKey(ImportRsaKey(encryptionKeyBase64));
            return;
        }

        if (environment.IsProduction())
        {
            throw new InvalidOperationException(
                "Production signing/encryption keys are required. Set the "
                + "SigningKeys__SigningKeyBase64 and SigningKeys__EncryptionKeyBase64 environment "
                + "variables to base64-encoded PEM RSA keys (openssl genrsa 2048 | base64 -w0, "
                + "run twice - signing and encryption keys must differ).");
        }

        builder.AddDevelopmentEncryptionCertificate()
            .AddDevelopmentSigningCertificate();
    }

    private static RsaSecurityKey ImportRsaKey(string base64Pem)
    {
        byte[] pem = Convert.FromBase64String(base64Pem);
        RSA rsa = RSA.Create();
        rsa.ImportFromPem(System.Text.Encoding.UTF8.GetString(pem));
        return new RsaSecurityKey(rsa);
    }

    /// <summary>
    /// In-memory Quartz store, not a persistent one: this service runs as a single
    /// instance with no replicas, so there is no clustering concern to coordinate -
    /// a restart just re-schedules the pruning job from scratch, which is harmless
    /// for a periodic maintenance job like this one.
    /// </summary>
    public static IServiceCollection AddOpenIddictQuartzScheduler(this IServiceCollection services)
    {
        services.AddQuartz(options =>
        {
            options.UseSimpleTypeLoader();
            options.UseInMemoryStore();
        });

        services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);

        return services;
    }
}