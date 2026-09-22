using AuthService.Domain;
using Microsoft.AspNetCore.Identity;

namespace AuthService.Web.Configuration;

/// <summary>
/// Plugs into Identity's own password-validation pipeline (AddPasswordValidator&lt;T&gt;
/// below), so registration, password reset, and any future admin-initiated password change
/// all get this check automatically through the same path RequiredLength/RequireUppercase
/// already run through - no separate call needed at each of those call sites.
/// </summary>
public class BreachedPasswordValidator(IPasswordBreachChecker checker) : IPasswordValidator<Account>
{
    public async Task<IdentityResult> ValidateAsync(UserManager<Account> manager, Account user, string? password)
    {
        if (password is null)
        {
            return IdentityResult.Success;
        }

        var breached = await checker.IsBreachedAsync(password, CancellationToken.None);
        return breached
            ? IdentityResult.Failed(new IdentityError
            {
                Code = "PasswordBreached",
                Description = "This password has appeared in a known data breach. Please choose a different one.",
            })
            : IdentityResult.Success;
    }
}
