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
public class RecoveryModel(
    SignInManager<Account> signInManager,
    TwoFactorService twoFactor,
    SecurityAuditService securityAudit,
    AuthSessionService authSessions)
    : PageModel
{
    [BindProperty]
    [Required]
    public string RecoveryCode { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string ReturnUrl { get; set; } = "/";

    public async Task<IActionResult> OnGetAsync()
    {
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

        var result = await twoFactor.CompleteWithRecoveryCodeAsync(RecoveryCode.Replace(" ", string.Empty, StringComparison.Ordinal));
        RecoveryCode = string.Empty;
        ModelState.Remove(nameof(RecoveryCode));

        if (result.IsLockedOut)
        {
            await securityAudit.RecordAccountLockedOutAsync(pendingUser, ClientIpAddress, cancellationToken);
            ModelState.AddModelError(string.Empty, "That recovery code didn't work. Each one can only be used once.");
            return Page();
        }

        if (!result.Succeeded)
        {
            await securityAudit.RecordLoginFailedAsync(
                pendingUser.Email!, "recovery_code_invalid", ClientIpAddress, cancellationToken);
            ModelState.AddModelError(string.Empty, "That recovery code didn't work. Each one can only be used once.");
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
