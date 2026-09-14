using AuthService.Domain;
using AuthService.Web.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Shared;
using Shared.EndpointResults;

namespace AuthService.Web.Controllers;

[ApiController]
[Route("auth")]
public class AccountController(UserManager<Account> userManager, SignInManager<Account> signInManager)
    : ControllerBase
{
    [HttpPost("register")]
    [EnableRateLimiting("auth")]
    public async Task<IResult> Register([FromBody] RegisterRequest request, CancellationToken cancellationToken)
    {
        var user = new Account
        {
            UserName = request.Email,
            Email = request.Email,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        var result = await userManager.CreateAsync(user, request.Password);
        if (!result.Succeeded)
        {
            return new ErrorResult(ToValidationError(result));
        }

        await userManager.AddToRoleAsync(user, RoleNames.Viewer);
        await signInManager.SignInAsync(user, isPersistent: true);

        return Results.NoContent();
    }

    [HttpPost("login")]
    [EnableRateLimiting("auth")]
    public async Task<IResult> Login([FromBody] LoginRequest request)
    {
        var result = await signInManager.PasswordSignInAsync(
            request.Email,
            request.Password,
            isPersistent: true,
            lockoutOnFailure: true);

        if (!result.Succeeded)
        {
            return new ErrorResult(Error.Authentication("auth.invalid_credentials", "Invalid email or password"));
        }

        return Results.NoContent();
    }

    [HttpPost("logout")]
    [Authorize(AuthenticationSchemes = "Identity.Application")]
    public async Task<IResult> Logout()
    {
        await signInManager.SignOutAsync();
        return Results.NoContent();
    }

    private static Error ToValidationError(IdentityResult result)
    {
        var messages = result.Errors.Select(error => new ErrorMessage(error.Code, error.Description));
        return Error.Validation(messages);
    }
}