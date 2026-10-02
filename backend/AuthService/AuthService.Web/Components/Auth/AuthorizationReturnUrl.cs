namespace AuthService.Web.Components.Auth;

internal static class AuthorizationReturnUrl
{
    public static bool IsValid(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl)
            || !returnUrl.StartsWith("/", StringComparison.Ordinal)
            || returnUrl.StartsWith("//", StringComparison.Ordinal)
            || returnUrl.StartsWith("/\\", StringComparison.Ordinal))
        {
            return false;
        }

        return returnUrl.Split('?', 2)[0].Equals("/connect/authorize", StringComparison.Ordinal);
    }
}
