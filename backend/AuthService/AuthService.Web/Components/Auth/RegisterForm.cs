using System.ComponentModel.DataAnnotations;

namespace AuthService.Web.Components.Auth;

internal sealed class RegisterForm
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;

    [Required]
    [Compare(nameof(Password), ErrorMessage = "Passwords do not match.")]
    public string ConfirmPassword { get; set; } = string.Empty;

    [Required]
    public string ReturnUrl { get; set; } = string.Empty;
}
