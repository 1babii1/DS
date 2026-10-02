using System.ComponentModel.DataAnnotations;

namespace AuthService.Web.Components.Auth;

internal sealed class PasswordConfirmationForm
{
    [Required]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;
}
