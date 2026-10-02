using System.ComponentModel.DataAnnotations;

namespace AuthService.Web.Components.Auth;

internal sealed class TwoFactorCodeForm
{
    [Required]
    [StringLength(7, MinimumLength = 6)]
    public string Code { get; set; } = string.Empty;

    public bool RememberClient { get; set; }

    [Required]
    public string ReturnUrl { get; set; } = string.Empty;
}
