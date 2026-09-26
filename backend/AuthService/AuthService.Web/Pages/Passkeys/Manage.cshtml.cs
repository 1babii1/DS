using AuthService.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AuthService.Web.Pages.Passkeys;

// Registration/listing/deletion all go through the JSON API directly from client-side JS -
// a WebAuthn ceremony is inherently browser-driven (navigator.credentials.create/get take
// real callback-based browser prompts a normal form POST can't produce), so this page is
// just the authenticated shell fetch() calls run against, not a source of server-rendered
// passkey data the way TwoFactor/Manage renders its setup state.
[Authorize(AuthenticationSchemes = "Identity.Application")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class ManageModel(UserManager<Account> userManager) : PageModel
{
    public string Email { get; private set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await userManager.GetUserAsync(User) ?? throw new InvalidOperationException("Signed-in user not found.");
        Email = user.Email!;
        return Page();
    }
}
