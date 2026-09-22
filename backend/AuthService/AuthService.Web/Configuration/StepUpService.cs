using AuthService.Domain;
using Microsoft.AspNetCore.Identity;

namespace AuthService.Web.Configuration;

/// <summary>
/// GitHub-style "sudo mode": an already-signed-in user re-proves who they are - via their
/// existing authenticator app if 2FA is enabled, or a one-time emailed code as the fallback
/// when it isn't - to open a short elevation window. AccountController exposes this over
/// /auth/step-up/*; Shared's StepUp authorization policy is what individual resource-service
/// endpoints (e.g. EmployeeController.Terminate) actually gate on, by reading the
/// elevated_until claim AuthorizationController stamps onto the next refreshed access token.
/// </summary>
public class StepUpService(UserManager<Account> userManager, IEmailSender emailSender)
{
    // Short enough that a stolen elevated token is only briefly useful, long enough to cover
    // "confirm, then immediately do the one thing you meant to do" - the same order of
    // magnitude as AWS/Stripe's own step-up re-auth windows.
    public static readonly TimeSpan ElevationWindow = TimeSpan.FromMinutes(10);

    /// <returns>False if the account already has 2FA enabled - it uses the authenticator
    /// code directly and has no use for an email fallback.</returns>
    public async Task<bool> RequestEmailCodeAsync(Account user, CancellationToken cancellationToken)
    {
        if (await userManager.GetTwoFactorEnabledAsync(user))
        {
            return false;
        }

        var code = await userManager.GenerateTwoFactorTokenAsync(user, TokenOptions.DefaultEmailProvider);
        await emailSender.SendStepUpCodeAsync(user.Email!, code!, cancellationToken);
        return true;
    }

    /// <returns>False if the code did not verify - the caller treats that as unauthorized
    /// rather than silently doing nothing.</returns>
    public async Task<bool> VerifyAsync(Account user, string code)
    {
        var provider = await userManager.GetTwoFactorEnabledAsync(user)
            ? userManager.Options.Tokens.AuthenticatorTokenProvider
            : TokenOptions.DefaultEmailProvider;

        if (!await userManager.VerifyTwoFactorTokenAsync(user, provider, code))
        {
            return false;
        }

        user.ElevatedUntil = DateTime.UtcNow.Add(ElevationWindow);
        await userManager.UpdateAsync(user);
        return true;
    }
}
