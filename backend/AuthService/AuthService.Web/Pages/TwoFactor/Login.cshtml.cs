using System.ComponentModel.DataAnnotations;
using AuthService.Domain;
using AuthService.Web.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace AuthService.Web.Pages.TwoFactor;

[AllowAnonymous]
[EnableRateLimiting("auth")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class LoginModel(
    SignInManager<Account> signInManager,
    TwoFactorService twoFactor,
    SecurityAuditService securityAudit,
    AuthSessionService authSessions)
    : PageModel
{
    [BindProperty]
    [Required]
    [StringLength(7, MinimumLength = 6)]
    public string Code { get; set; } = string.Empty;

    [BindProperty]
    public bool RememberClient { get; set; }

    [BindProperty(SupportsGet = true)]
    public string ReturnUrl { get; set; } = "/";

    public async Task<IActionResult> OnGetAsync()
    {
        // ReturnUrl arrives here from SignIn's own redirect, but this page's GET also
        // accepts it straight from the query string - an attacker linking directly to
        // this page must not be able to smuggle an off-site ReturnUrl past SignIn's own
        // validation, so it is re-checked here rather than trusted as already-safe.
        if (!IsAuthorizationReturnUrl()) return BadRequest();

        return await signInManager.GetTwoFactorAuthenticationUserAsync() is null
            ? RedirectToPage("/SignIn", new { ReturnUrl })
            : Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!IsAuthorizationReturnUrl()) return BadRequest();

        var pendingUser = await signInManager.GetTwoFactorAuthenticationUserAsync();
        if (pendingUser is null)
        {
            return RedirectToPage("/SignIn", new { ReturnUrl });
        }

        if (!ModelState.IsValid) return Page();

        // Authenticator apps show the code with a space (e.g. "123 456"); strip
        // whitespace so a copy-paste from one still verifies.
        var code = Code.Replace(" ", string.Empty, StringComparison.Ordinal);
        var result = await twoFactor.CompleteWithAuthenticatorCodeAsync(code, RememberClient);
        Code = string.Empty;
        ModelState.Remove(nameof(Code));

        if (result.IsLockedOut)
        {
            await securityAudit.RecordAccountLockedOutAsync(pendingUser, ClientIpAddress, cancellationToken);
            ModelState.AddModelError(string.Empty, "That code didn't work. Check your authenticator app and try again.");
            return Page();
        }

        if (!result.Succeeded)
        {
            await securityAudit.RecordLoginFailedAsync(
                pendingUser.Email!, "two_factor_invalid", ClientIpAddress, cancellationToken);
            ModelState.AddModelError(string.Empty, "That code didn't work. Check your authenticator app and try again.");
            return Page();
        }

        await securityAudit.RecordLoginSucceededAsync(pendingUser, ClientIpAddress, cancellationToken);
        await authSessions.EstablishAsync(pendingUser, ClientIpAddress, UserAgent, cancellationToken);
        return LocalRedirect(ReturnUrl);
    }

    private bool IsAuthorizationReturnUrl() => Url.IsLocalUrl(ReturnUrl)
        && ReturnUrl.Split('?')[0].Equals("/connect/authorize", StringComparison.Ordinal);

    private string? ClientIpAddress => HttpContext.Connection.RemoteIpAddress?.ToString();

    private string? UserAgent => Request.Headers.UserAgent.ToString() is { Length: > 0 } value ? value : null;
}
