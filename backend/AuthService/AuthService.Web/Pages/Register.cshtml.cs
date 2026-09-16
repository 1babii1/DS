using System.ComponentModel.DataAnnotations;
using AuthService.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AuthService.Web.Pages;

[AllowAnonymous]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class RegisterModel(UserManager<Account> userManager, SignInManager<Account> signInManager) : PageModel
{
    [BindProperty]
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [BindProperty]
    [Required]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    [BindProperty]
    [Required]
    [Compare(nameof(Password), ErrorMessage = "Passwords do not match.")]
    [DataType(DataType.Password)]
    public string ConfirmPassword { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string ReturnUrl { get; set; } = string.Empty;

    public IActionResult OnGet() => IsAuthorizationReturnUrl() ? Page() : BadRequest();

    public async Task<IActionResult> OnPostAsync()
    {
        if (!IsAuthorizationReturnUrl()) return BadRequest();
        if (!ModelState.IsValid) return Page();

        var user = new Account
        {
            UserName = Email,
            Email = Email,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var result = await userManager.CreateAsync(user, Password);
        Password = string.Empty;
        ConfirmPassword = string.Empty;
        ModelState.Remove(nameof(Password));
        ModelState.Remove(nameof(ConfirmPassword));

        if (!result.Succeeded)
        {
            foreach (var error in result.Errors) ModelState.AddModelError(string.Empty, error.Description);
            return Page();
        }

        var roleResult = await userManager.AddToRoleAsync(user, RoleNames.Viewer);
        if (!roleResult.Succeeded)
        {
            foreach (var error in roleResult.Errors) ModelState.AddModelError(string.Empty, error.Description);
            return Page();
        }

        await signInManager.SignInAsync(user, isPersistent: true);
        return LocalRedirect(ReturnUrl);
    }

    private bool IsAuthorizationReturnUrl() => Url.IsLocalUrl(ReturnUrl)
        && ReturnUrl.Split('?')[0].Equals("/connect/authorize", StringComparison.Ordinal);
}
