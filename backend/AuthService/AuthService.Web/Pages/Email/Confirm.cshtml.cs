using AuthService.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace AuthService.Web.Pages.Email;

[AllowAnonymous]
[EnableRateLimiting("auth")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class ConfirmModel(UserManager<Account> userManager) : PageModel
{
    public bool Confirmed { get; private set; }

    // A single GET consumes the link, matching the standard ASP.NET Core Identity
    // pattern (the same shape as the framework's own scaffolded Identity UI) - there is
    // nothing further for the user to submit, so a second confirming click/POST would
    // only add friction without adding safety.
    public async Task<IActionResult> OnGetAsync(string? email, string? token)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(token))
        {
            return BadRequest();
        }

        var user = await userManager.FindByEmailAsync(email);
        if (user is not null)
        {
            var result = await userManager.ConfirmEmailAsync(user, token);
            Confirmed = result.Succeeded;
        }

        // A missing account renders the same "link is invalid" outcome as a bad token -
        // Confirmed stays false either way, so the response does not reveal which one it was.
        return Page();
    }
}
