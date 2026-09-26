namespace AuthService.Web.Configuration;

// The confidential OIDC client for the Next.js/Auth.js BFF (issue #13) - disabled
// by default so a deployment with no injected configuration behaves exactly as it
// did before this existed, same reasoning as SigningKeys falling back to a dev
// certificate rather than starting broken.
public sealed class WebClientOptions
{
    public const string SectionName = "Auth:WebClient";

    public const string ClientId = "portfolio-web";

    public bool Enabled { get; set; }

    public string FrontendOrigin { get; set; } = "http://localhost:3000";

    public string ClientSecret { get; set; } = string.Empty;

    public bool IsValid(bool isLocal)
    {
        if (!Enabled)
        {
            return true;
        }

        return ClientSecret.Length >= 32
            && Uri.TryCreate(FrontendOrigin, UriKind.Absolute, out var origin)
            && string.IsNullOrEmpty(origin.UserInfo)
            && origin.AbsolutePath == "/"
            && string.IsNullOrEmpty(origin.Query)
            && string.IsNullOrEmpty(origin.Fragment)
            && (origin.Scheme == "https" || (isLocal && origin.Scheme == "http" && origin.IsLoopback));
    }
}
