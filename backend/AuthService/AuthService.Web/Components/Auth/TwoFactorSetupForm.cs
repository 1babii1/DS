using System.ComponentModel.DataAnnotations;

namespace AuthService.Web.Components.Auth;

internal sealed class TwoFactorSetupForm
{
    [Required]
    [StringLength(7, MinimumLength = 6)]
    public string Code { get; set; } = string.Empty;
}
