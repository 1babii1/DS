using System.Text.Encodings.Web;
using AuthService.Domain;
using AuthService.Web.Contracts;
using Microsoft.AspNetCore.Identity;

namespace AuthService.Web.Configuration;

/// <summary>
/// Owns TOTP setup/enable/disable and the two shapes of completing a two-factor login
/// (authenticator code, recovery code) - shared by AccountController and the matching
/// Razor Pages for the same reason AccountRecoveryService is shared: one place for
/// decisions that matter for account security, not two that can drift apart.
/// </summary>
public class TwoFactorService(UserManager<Account> userManager, SignInManager<Account> signInManager)
{
    private const string Issuer = "DS";

    /// <summary>
    /// Ensures an authenticator key exists and returns it formatted for display, plus the
    /// otpauth:// URI an authenticator app can import directly. Calling this repeatedly
    /// before Enable is a no-op past the first call - it only generates a fresh key when
    /// none exists yet, so refreshing the setup page does not invalidate a code the user
    /// already scanned.
    /// </summary>
    public async Task<TwoFactorSetupResponse> GetOrCreateSetupAsync(Account user)
    {
        var key = await userManager.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrEmpty(key))
        {
            await userManager.ResetAuthenticatorKeyAsync(user);
            key = await userManager.GetAuthenticatorKeyAsync(user);
        }

        return new TwoFactorSetupResponse(FormatForDisplay(key!), BuildAuthenticatorUri(user.Email!, key!));
    }

    /// <returns>Recovery codes on success, null if the code did not verify.</returns>
    public async Task<IReadOnlyList<string>?> EnableAsync(Account user, string code)
    {
        var isValid = await userManager.VerifyTwoFactorTokenAsync(
            user, userManager.Options.Tokens.AuthenticatorTokenProvider, code);
        if (!isValid)
        {
            return null;
        }

        await userManager.SetTwoFactorEnabledAsync(user, true);
        var recoveryCodes = await userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
        return recoveryCodes!.ToList();
    }

    /// <returns>False if the password did not match - the caller treats that as
    /// unauthorized rather than silently doing nothing.</returns>
    public async Task<bool> DisableAsync(Account user, string password)
    {
        if (!await userManager.CheckPasswordAsync(user, password))
        {
            return false;
        }

        await userManager.SetTwoFactorEnabledAsync(user, false);

        // Not required for PasswordSignInAsync to stop challenging for 2FA (that check
        // short-circuits on the flag alone, verified directly against UserManager rather
        // than assumed) - this is defense in depth: re-enabling later issues a fresh
        // secret instead of reusing one that may already be sitting in an old phone
        // backup or screenshot.
        await userManager.ResetAuthenticatorKeyAsync(user);

        return true;
    }

    public async Task<IReadOnlyList<string>?> RegenerateRecoveryCodesAsync(Account user, string password)
    {
        if (!await userManager.CheckPasswordAsync(user, password))
        {
            return null;
        }

        var codes = await userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
        return codes!.ToList();
    }

    /// <summary>
    /// Completes a login PasswordSignInAsync left pending (SignInResult.RequiresTwoFactor)
    /// by reading the short-lived Identity.TwoFactorUserId cookie SignInManager already
    /// set - there is no email/password to re-check here, only the code.
    /// </summary>
    public async Task<SignInResult> CompleteWithAuthenticatorCodeAsync(string code, bool rememberClient) =>
        await signInManager.TwoFactorAuthenticatorSignInAsync(code, isPersistent: true, rememberClient);

    public async Task<SignInResult> CompleteWithRecoveryCodeAsync(string recoveryCode) =>
        await signInManager.TwoFactorRecoveryCodeSignInAsync(recoveryCode);

    private static string FormatForDisplay(string key)
    {
        // Groups of 4 is the display convention every authenticator app's own manual-entry
        // screen uses - purely cosmetic, does not affect the key an app actually imports.
        var groups = Enumerable.Range(0, (key.Length + 3) / 4).Select(i => key.Substring(i * 4, Math.Min(4, key.Length - (i * 4))));
        return string.Join(' ', groups);
    }

    private static string BuildAuthenticatorUri(string email, string key) =>
        $"otpauth://totp/{UrlEncoder.Default.Encode(Issuer)}:{UrlEncoder.Default.Encode(email)}" +
        $"?secret={key}&issuer={UrlEncoder.Default.Encode(Issuer)}&digits=6";
}
