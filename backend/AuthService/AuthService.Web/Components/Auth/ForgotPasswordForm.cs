using System.ComponentModel.DataAnnotations;

namespace AuthService.Web.Components.Auth;

internal sealed class ForgotPasswordForm
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;
}
