using System.ComponentModel.DataAnnotations;
using AuthService.Web.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace AuthService.Web.Pages.Password;

[AllowAnonymous]
[EnableRateLimiting("auth")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class ForgotModel(AccountRecoveryService recovery) : PageModel
{
    [BindProperty]
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string ReturnUrl { get; set; } = "/";

    /// <summary>Always set once the form is submitted with a valid-looking email, whether
    /// or not an account actually exists - the enumeration-safe answer is identical
    /// either way, decided once in AccountRecoveryService.</summary>
    public bool Submitted { get; private set; }

    public IActionResult OnGet() => Page();

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return Page();

        await recovery.SendPasswordResetIfEligibleAsync(Email, cancellationToken);

        Submitted = true;
        return Page();
    }
}
