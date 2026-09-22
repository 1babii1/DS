using AuthService.Web.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace AuthService.Web.Pages.Email;

[AllowAnonymous]
[EnableRateLimiting("auth")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class ConfirmChangeModel(EmailChangeService emailChange) : PageModel
{
    public bool Confirmed { get; private set; }

    // Same shape as Email/Confirm: a single GET consumes the link, nothing further to submit.
    public async Task<IActionResult> OnGetAsync(string? userId, string? newEmail, string? token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(newEmail) || string.IsNullOrWhiteSpace(token)
            || !Guid.TryParse(userId, out var accountId))
        {
            return BadRequest();
        }

        Confirmed = await emailChange.CompleteChangeAsync(
            accountId, newEmail, token, HttpContext.Connection.RemoteIpAddress?.ToString(), cancellationToken);
        return Page();
    }
}
