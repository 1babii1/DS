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
public class ResetModel(AccountRecoveryService recovery) : PageModel
{
    [BindProperty(SupportsGet = true)]
    [Required]
    public string Email { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    [Required]
    public string Token { get; set; } = string.Empty;

    [BindProperty]
    [Required]
    [DataType(DataType.Password)]
    public string NewPassword { get; set; } = string.Empty;

    [BindProperty]
    [Required]
    [Compare(nameof(NewPassword), ErrorMessage = "Passwords do not match.")]
    [DataType(DataType.Password)]
    public string ConfirmPassword { get; set; } = string.Empty;

    public bool Completed { get; private set; }

    public IActionResult OnGet() =>
        string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(Token) ? BadRequest() : Page();

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return Page();

        var result = await recovery.ResetPasswordAsync(
            Email, Token, NewPassword, HttpContext.Connection.RemoteIpAddress?.ToString(), cancellationToken);
        NewPassword = string.Empty;
        ConfirmPassword = string.Empty;
        ModelState.Remove(nameof(NewPassword));
        ModelState.Remove(nameof(ConfirmPassword));

        if (!result.Succeeded)
        {
            // Generic on purpose, same reasoning as AccountController.ResetPassword: a
            // missing account and an invalid/expired token must look identical here.
            ModelState.AddModelError(string.Empty, "This link is invalid or has expired. Request a new one.");
            return Page();
        }

        Completed = true;
        return Page();
    }
}
