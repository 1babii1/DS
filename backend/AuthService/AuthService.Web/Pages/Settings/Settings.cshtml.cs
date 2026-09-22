using AuthService.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AuthService.Web.Pages.Settings;

// Change-email and delete-account both go through the JSON API from client-side JS - same
// reasoning as Passkeys/Manage: this page is just the authenticated shell.
[Authorize(AuthenticationSchemes = "Identity.Application")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class SettingsModel(UserManager<Account> userManager) : PageModel
{
    public string Email { get; private set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await userManager.GetUserAsync(User) ?? throw new InvalidOperationException("Signed-in user not found.");
        Email = user.Email!;
        return Page();
    }
}
