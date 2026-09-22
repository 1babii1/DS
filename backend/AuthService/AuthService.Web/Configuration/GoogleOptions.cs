namespace AuthService.Web.Configuration;

/// <summary>
/// "Sign in with Google" - disabled by default so a deployment with no injected client
/// credentials behaves exactly as it did before this existed, same reasoning as
/// WebClientOptions.Enabled and SigningKeys falling back to a dev certificate.
/// </summary>
public class GoogleOptions
{
    public const string SectionName = "Auth:Google";

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    public bool Enabled => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}
