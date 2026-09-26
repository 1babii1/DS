using System.ComponentModel.DataAnnotations;
using AuthService.Domain;
using AuthService.Web.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace AuthService.Web.Pages;

[AllowAnonymous]
[EnableRateLimiting("auth")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class SignInModel(
    UserManager<Account> userManager,
    SignInManager<Account> signInManager,
    AccountRecoveryService recovery,
    SecurityAuditService securityAudit,
    IOptions<GoogleOptions> googleOptions,
    AuthSessionService authSessions)
    : PageModel
{
    [BindProperty]
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [BindProperty]
    [Required]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string ReturnUrl { get; set; } = string.Empty;

    public bool GoogleSignInEnabled => googleOptions.Value.Enabled;

    public IActionResult OnGet() => IsAuthorizationReturnUrl() ? Page() : BadRequest();

    /// <summary>Set when the account exists and the password was correct but the email
    /// is not confirmed yet - the page offers a resend link instead of the generic
    /// failure message, which is safe to show since the caller already proved they
    /// know the password.</summary>
    public bool EmailNotConfirmed { get; private set; }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!IsAuthorizationReturnUrl()) return BadRequest();
        if (!ModelState.IsValid) return Page();

        var result = await signInManager.PasswordSignInAsync(Email, Password, isPersistent: false, lockoutOnFailure: true);
        Password = string.Empty;
        ModelState.Remove(nameof(Password));

        if (result.RequiresTwoFactor)
        {
            return RedirectToPage("/TwoFactor/Login", new { ReturnUrl });
        }

        if (result.Succeeded)
        {
            var user = await userManager.FindByEmailAsync(Email)
                ?? throw new InvalidOperationException("Signed-in user not found.");
            await securityAudit.RecordLoginSucceededAsync(user, ClientIpAddress, cancellationToken);
            await authSessions.EstablishAsync(user, ClientIpAddress, UserAgent, cancellationToken);
            return LocalRedirect(ReturnUrl);
        }

        if (result.IsLockedOut)
        {
            var lockedOutUser = await userManager.FindByEmailAsync(Email);
            if (lockedOutUser is not null)
            {
                await securityAudit.RecordAccountLockedOutAsync(lockedOutUser, ClientIpAddress, cancellationToken);
            }

            ModelState.AddModelError(string.Empty, "We couldn't sign you in. Check your details or try again later.");
            return Page();
        }

        if (result.IsNotAllowed)
        {
            await securityAudit.RecordLoginFailedAsync(Email, "email_not_confirmed", ClientIpAddress, cancellationToken);
            EmailNotConfirmed = true;
            ModelState.AddModelError(string.Empty, "Please confirm your email before signing in.");
            return Page();
        }

        await securityAudit.RecordLoginFailedAsync(Email, "invalid_credentials", ClientIpAddress, cancellationToken);
        ModelState.AddModelError(string.Empty, "We couldn't sign you in. Check your details or try again later.");
        return Page();
    }

    private string? ClientIpAddress => HttpContext.Connection.RemoteIpAddress?.ToString();

    private string? UserAgent => Request.Headers.UserAgent.ToString() is { Length: > 0 } value ? value : null;

    /// <summary>Shown as a link only after a NotAllowed result, so it is never offered as
    /// a generic action on the page - reusing the same rate-limited "auth" policy as
    /// every other credential-adjacent endpoint on this page.</summary>
    public async Task<IActionResult> OnPostResendConfirmationAsync(CancellationToken cancellationToken)
    {
        if (!IsAuthorizationReturnUrl()) return BadRequest();

        if (!string.IsNullOrWhiteSpace(Email))
        {
            await recovery.ResendConfirmationIfEligibleAsync(Email, cancellationToken);
        }

        EmailNotConfirmed = true;
        Password = string.Empty;
        ModelState.Clear();
        ModelState.AddModelError(string.Empty, "If that account needs confirming, a new link is on its way.");
        return Page();
    }

    private bool IsAuthorizationReturnUrl() => Url.IsLocalUrl(ReturnUrl)
        && ReturnUrl.Split('?')[0].Equals("/connect/authorize", StringComparison.Ordinal);
}
