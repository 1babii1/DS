using System.ComponentModel.DataAnnotations;

namespace AuthService.Web.Components.Auth;

internal sealed class RecoveryCodeForm
{
    [Required]
    public string RecoveryCode { get; set; } = string.Empty;

    [Required]
    public string ReturnUrl { get; set; } = string.Empty;
}
