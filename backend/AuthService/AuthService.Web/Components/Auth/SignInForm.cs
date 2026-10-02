using System.ComponentModel.DataAnnotations;

namespace AuthService.Web.Components.Auth;

internal sealed class SignInForm
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;

    [Required]
    public string ReturnUrl { get; set; } = string.Empty;
}
