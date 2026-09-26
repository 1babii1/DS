using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Shared.Security;

/// <summary>
/// The "Auth" configuration section every service behind the platform's OIDC provider needs.
/// </summary>
public class JwtAuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>OIDC discovery document, used to fetch the issuer's signing keys.</summary>
    [Required(AllowEmptyStrings = false)]
    public string MetadataAddress { get; set; } = null!;

    [Required(AllowEmptyStrings = false)]
    public string Issuer { get; set; } = null!;

    /// <summary>This service's own API resource name, as seeded into OpenIddict.</summary>
    [Required(AllowEmptyStrings = false)]
    public string Audience { get; set; } = null!;
}

/// <summary>
/// One definition of "how this platform authenticates an API call", instead of the same
/// twelve lines pasted into seven Program.cs files.
/// <para>
/// Bound through the options pattern with <c>ValidateOnStart</c> rather than read directly
/// out of <c>IConfiguration</c>: a missing or misspelled Auth value used to be assigned
/// straight onto <c>JwtBearerOptions</c> as null, so the service started happily and only
/// failed later, on the first request carrying a token - at which point the error points at
/// token validation rather than at the configuration that actually caused it.
/// </para>
/// </summary>
public static class JwtAuthenticationExtensions
{
    public static IServiceCollection AddPlatformJwtAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        Action<JwtBearerOptions>? configureJwtBearer = null)
    {
        services.AddOptions<JwtAuthOptions>()
            .Bind(configuration.GetSection(JwtAuthOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtAuthOptions>>((jwt, auth) =>
            {
                JwtAuthOptions settings = auth.Value;

                jwt.MapInboundClaims = false;
                jwt.MetadataAddress = settings.MetadataAddress;
                jwt.RequireHttpsMetadata = environment.IsProduction();
                jwt.TokenValidationParameters.ValidIssuer = settings.Issuer;
                jwt.TokenValidationParameters.ValidAudience = settings.Audience;
                jwt.TokenValidationParameters.RoleClaimType = "role";
                jwt.TokenValidationParameters.NameClaimType = "name";

                configureJwtBearer?.Invoke(jwt);
            });

        return services;
    }
}
