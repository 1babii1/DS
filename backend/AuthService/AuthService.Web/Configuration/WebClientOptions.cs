namespace AuthService.Web.Configuration;

public sealed class WebClientOptions
{
    public const string SectionName = "Auth:WebClient";

    public const string ClientId = "portfolio-web";

    public bool Enabled { get; set; }

    public string FrontendOrigin { get; set; } = "http://localhost:3000";

    public string ClientSecret { get; set; } = string.Empty;

    public bool IsValid(bool isLocal)
    {
        if (!Enabled) return true;
        return ClientSecret.Length >= 32
            && Uri.TryCreate(FrontendOrigin, UriKind.Absolute, out var origin)
            && string.IsNullOrEmpty(origin.UserInfo)
            && origin.AbsolutePath == "/"
            && string.IsNullOrEmpty(origin.Query)
            && string.IsNullOrEmpty(origin.Fragment)
            && (origin.Scheme == "https" || (isLocal && origin.Scheme == "http" && origin.IsLoopback));
    }
}
