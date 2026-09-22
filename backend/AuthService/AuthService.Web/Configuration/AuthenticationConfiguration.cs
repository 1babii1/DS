using System.Security.Claims;
using System.Security.Cryptography;
using AuthService.Domain;
using AuthService.Infrastructure.Postgres;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
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

    /// <summary>Name of the token provider password-reset links are generated and redeemed through.</summary>
    public const string PasswordResetTokenProvider = "PasswordReset";

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

                // PasswordSignInAsync (both AccountController.Login and SignInModel) rejects
                // an unconfirmed account with SignInResult.NotAllowed. This only takes effect
                // because Register/RegisterModel no longer call SignInManager.SignInAsync
                // directly - that method does not consult this flag, so leaving the old
                // auto-sign-in in place would have silently bypassed it.
                options.SignIn.RequireConfirmedAccount = true;

                // Without this, ResetPasswordAsync/GeneratePasswordResetTokenAsync resolve
                // Tokens.PasswordResetTokenProvider's *default* value ("Default") instead of
                // the provider registered below under this name - silently using the shared
                // 1-day-lifespan provider and making the dedicated one dead code.
                options.Tokens.PasswordResetTokenProvider = PasswordResetTokenProvider;

                options.ClaimsIdentity.UserIdClaimType = OpenIddictConstants.Claims.Subject;
                options.ClaimsIdentity.UserNameClaimType = OpenIddictConstants.Claims.Name;
                options.ClaimsIdentity.EmailClaimType = OpenIddictConstants.Claims.Email;
                options.ClaimsIdentity.RoleClaimType = OpenIddictConstants.Claims.Role;
            })
            .AddEntityFrameworkStores<AuthDbContext>()
            .AddDefaultTokenProviders()

            // A password-reset token is a stronger capability than an email-confirmation
            // token (it changes a credential, not just a flag), so it gets its own shorter
            // lifespan instead of sharing the default provider's 1-day window that
            // GenerateEmailConfirmationTokenAsync also uses. DataProtectorTokenProvider<TUser>
            // resolves a plain (unnamed) IOptions<DataProtectionTokenProviderOptions> - every
            // instance registered under that same concrete type shares one lifespan
            // regardless of the provider *name* used here, so a distinct lifespan needs a
            // distinct options type, not just AddOptions(name). PasswordResetTokenProvider<T>
            // below exists solely to give this provider its own IOptions<T> to resolve.
            .AddTokenProvider<PasswordResetTokenProvider<Account>>(PasswordResetTokenProvider)

            // Step-up ("sudo mode") verification's email-code fallback for accounts without
            // 2FA enabled. Unlike PasswordResetTokenProvider, this needs no dedicated options
            // type: EmailTokenProvider<TUser> extends TotpSecurityStampBasedTokenProvider, a
            // different base class that ignores DataProtectionTokenProviderOptions.TokenLifespan
            // entirely and instead uses RFC 6238's fixed ~3-minute step window internally -
            // verified directly against a real UserManager rather than assumed, the same way
            // PasswordResetTokenProvider's own trap was caught.
            .AddTokenProvider<EmailTokenProvider<Account>>(TokenOptions.DefaultEmailProvider)

            // Runs automatically through CreateAsync/ResetPasswordAsync/ChangePasswordAsync
            // alongside the length/complexity validators above - see BreachedPasswordValidator.
            .AddPasswordValidator<BreachedPasswordValidator>();

        services.Configure<PasswordResetTokenProviderOptions>(
            options => options.TokenLifespan = TimeSpan.FromHours(1));

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
            options.LoginPath = "/auth/sign-in";
            options.Events.OnRedirectToLogin = context =>
            {
                // An unauthenticated /connect/authorize request is a browser
                // navigation, not an API call - redirect to the sign-in page like
                // any other cookie-authenticated route, instead of 401ing a request
                // the browser can't retry with credentials on its own.
                if (context.Request.Path.StartsWithSegments("/connect/authorize"))
                {
                    context.Response.Redirect(context.RedirectUri);
                    return Task.CompletedTask;
                }

                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };

            // AddIdentity already wires this event to SecurityStampValidator.ValidatePrincipalAsync
            // (the check that makes password reset / "sign out everywhere" work by rejecting a
            // stale stamp) - chaining onto it here rather than replacing it, so per-session
            // revocation (AuthSessionService) adds a second, independent reason a cookie can be
            // rejected instead of silently dropping the first one this project already relies on.
            options.Events.OnValidatePrincipal = async context =>
            {
                await SecurityStampValidator.ValidatePrincipalAsync(context);
                if (context.Principal is null)
                {
                    return;
                }

                var sessionIdClaim = context.Principal.FindFirstValue(AuthSessionService.SessionIdClaimType);
                if (sessionIdClaim is null || !Guid.TryParse(sessionIdClaim, out var sessionId))
                {
                    // No session_id claim: a cookie issued before this feature shipped, or by
                    // a path that does not call AuthSessionService.EstablishAsync. Nothing to
                    // check against, so this only ever narrows validity, never grants it.
                    return;
                }

                var sessions = context.HttpContext.RequestServices.GetRequiredService<AuthSessionService>();
                if (!await sessions.IsValidAsync(sessionId, context.HttpContext.RequestAborted))
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
                }
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

                // Signing/encryption credentials are NOT added here - see
                // AddDynamicSigningKeys below for why they need to come from a callback that
                // can re-run after a rotation, not a one-time call at server-configuration time.
            })
            .AddValidation(options =>
            {
                options.UseLocalServer();
                options.UseAspNetCore();
            });

        services.AddDynamicSigningKeys();

        return services;
    }

    /// <summary>
    /// Loads the currently-active signing/encryption keys from SigningKeyStore into
    /// OpenIddict's server options - registered as a genuine Configure callback (not a
    /// one-shot call inside AddServer) so it can be forced to re-run after a rotation.
    /// OpenIddictServerDispatcher/Factory resolve these options through
    /// IOptionsMonitor&lt;OpenIddictServerOptions&gt;, not IOptions&lt;T&gt; (confirmed via
    /// reflection, not assumed), which is exactly what makes SigningKeyStore's cache
    /// invalidation take effect on the very next request instead of needing a restart.
    /// Keys are registered newest-first: OpenIddict signs new tokens with the FIRST credential
    /// in each list and validates incoming tokens against all of them - the end-to-end rotation
    /// test proves this ordering rather than assuming it.
    /// </summary>
    private static IServiceCollection AddDynamicSigningKeys(this IServiceCollection services)
    {
        services.AddOptions<OpenIddictServerOptions>()
            .Configure<IServiceScopeFactory>((options, scopeFactory) =>
            {
                using var scope = scopeFactory.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<SigningKeyStore>();

                // Newest first: empirically confirmed (SigningKeyRotationTests) that OpenIddict
                // signs new tokens with the *first* credential in the list and validates
                // incoming tokens against all of them - GetActiveKeysAsync itself returns
                // oldest-first (RotateIfDueAsync relies on that ordering separately), so this
                // reverses it rather than changing the shared method's own contract.
                var signingKeys = store.GetActiveKeysAsync(SigningKeyPurpose.Signing, CancellationToken.None)
                    .GetAwaiter().GetResult();
                foreach (var key in signingKeys.Reverse())
                {
                    options.SigningCredentials.Add(
                        new SigningCredentials(store.ImportKey(key), SecurityAlgorithms.RsaSha256));
                }

                var encryptionKeys = store.GetActiveKeysAsync(SigningKeyPurpose.Encryption, CancellationToken.None)
                    .GetAwaiter().GetResult();
                foreach (var key in encryptionKeys.Reverse())
                {
                    options.EncryptionCredentials.Add(new EncryptingCredentials(
                        store.ImportKey(key), SecurityAlgorithms.RsaOAEP, SecurityAlgorithms.Aes256CbcHmacSha512));
                }
            });

        return services;
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

            // Checks daily whether a signing/encryption key rotation is due - see
            // SigningKeyRotationJob and SigningKeyStore.RotationInterval.
            var rotationJobKey = new JobKey(nameof(SigningKeyRotationJob));
            options.AddJob<SigningKeyRotationJob>(job => job.WithIdentity(rotationJobKey))
                .AddTrigger(trigger => trigger
                    .ForJob(rotationJobKey)
                    .WithSimpleSchedule(schedule => schedule.WithInterval(TimeSpan.FromHours(24)).RepeatForever()));
        });

        services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);

        return services;
    }
}
