using System.ComponentModel.DataAnnotations;
using AuthService.Domain;
using AuthService.Web.Configuration;
using AuthService.Web.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace AuthService.Web.Pages.TwoFactor;

[Authorize(AuthenticationSchemes = "Identity.Application")]
[EnableRateLimiting("auth")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class ManageModel(UserManager<Account> userManager, Configuration.TwoFactorService twoFactor) : PageModel
{
    public bool Enabled { get; private set; }

    public TwoFactorSetupResponse? Setup { get; private set; }

    public IReadOnlyList<string>? RecoveryCodes { get; private set; }

    [BindProperty]
    [Required]
    [StringLength(7, MinimumLength = 6)]
    public string Code { get; set; } = string.Empty;

    [BindProperty]
    [Required]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await CurrentUserAsync();
        Enabled = await userManager.GetTwoFactorEnabledAsync(user);
        if (!Enabled)
        {
            Setup = await twoFactor.GetOrCreateSetupAsync(user);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostEnableAsync()
    {
        var user = await CurrentUserAsync();
        Setup = await twoFactor.GetOrCreateSetupAsync(user);

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var codes = await twoFactor.EnableAsync(user, Code.Replace(" ", string.Empty, StringComparison.Ordinal));
        Code = string.Empty;
        ModelState.Remove(nameof(Code));

        if (codes is null)
        {
            ModelState.AddModelError(string.Empty, "That code didn't work. Check your authenticator app and try again.");
            return Page();
        }

        Enabled = true;
        Setup = null;
        RecoveryCodes = codes;
        return Page();
    }

    public async Task<IActionResult> OnPostDisableAsync()
    {
        var user = await CurrentUserAsync();

        if (!await twoFactor.DisableAsync(user, Password))
        {
            Password = string.Empty;
            ModelState.Remove(nameof(Password));
            ModelState.AddModelError(string.Empty, "Incorrect password.");
            Enabled = true;
            return Page();
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRegenerateCodesAsync()
    {
        var user = await CurrentUserAsync();
        Enabled = true;

        var codes = await twoFactor.RegenerateRecoveryCodesAsync(user, Password);
        Password = string.Empty;
        ModelState.Remove(nameof(Password));

        if (codes is null)
        {
            ModelState.AddModelError(string.Empty, "Incorrect password.");
            return Page();
        }

        RecoveryCodes = codes;
        return Page();
    }

    private async Task<Account> CurrentUserAsync() =>
        await userManager.GetUserAsync(User) ?? throw new InvalidOperationException("Signed-in user not found.");
}
