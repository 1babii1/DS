using System.ComponentModel.DataAnnotations;
using AuthService.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AuthService.Web.Pages;

[AllowAnonymous]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class SignInModel(SignInManager<Account> signInManager) : PageModel
{
    [BindProperty]
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [BindProperty]
    [Required]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string ReturnUrl { get; set; } = string.Empty;

    public IActionResult OnGet() => IsAuthorizationReturnUrl() ? Page() : BadRequest();

    public async Task<IActionResult> OnPostAsync()
    {
        if (!IsAuthorizationReturnUrl()) return BadRequest();
        if (!ModelState.IsValid) return Page();

        var result = await signInManager.PasswordSignInAsync(Email, Password, isPersistent: false, lockoutOnFailure: true);
        Password = string.Empty;
        ModelState.Remove(nameof(Password));
        if (result.Succeeded) return LocalRedirect(ReturnUrl);
        ModelState.AddModelError(string.Empty, "We couldn't sign you in. Check your details or try again later.");
        return Page();
    }

    private bool IsAuthorizationReturnUrl() => Url.IsLocalUrl(ReturnUrl)
        && ReturnUrl.Split('?')[0].Equals("/connect/authorize", StringComparison.Ordinal);
}
