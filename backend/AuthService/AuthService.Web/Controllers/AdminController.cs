using AuthService.Domain;
using AuthService.Web.Configuration;
using AuthService.Web.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Shared;
using Shared.EndpointResults;
using Shared.Security;

namespace AuthService.Web.Controllers;

// Bearer/OIDC auth (the default scheme, same as every other resource-service controller
// in this system), not the Identity.Application cookie the login-UI endpoints use - the
// elevated_until claim [RequireStepUp] checks only ever exists on an access token minted
// by AuthorizationController.Exchange, never on the cookie's ClaimsPrincipal. Matches the
// one existing precedent for AuthService validating its own tokens: AuthorizationController
// .Userinfo already authenticates this same way for the same reason.
[ApiController]
[Route("admin/accounts")]
[Authorize]
[RequireAdmin]
public class AdminController(
    UserManager<Account> userManager,
    AdminAccountService adminAccounts)
    : ControllerBase
{
    private string? ClientIpAddress => HttpContext.Connection.RemoteIpAddress?.ToString();

    [HttpGet]
    public async Task<IResult> List(
        [FromQuery] string? search, [FromQuery] int? page, [FromQuery] int? size, CancellationToken cancellationToken) =>
        Results.Ok(await adminAccounts.SearchAsync(search, page, size, cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<IResult> GetById(Guid id)
    {
        var account = await adminAccounts.GetByIdAsync(id);
        return account is null ? Results.NotFound() : Results.Ok(account);
    }

    [HttpPut("{id:guid}/roles")]
    [RequireStepUp]
    [EnableRateLimiting("auth")]
    public async Task<IResult> SetRoles(Guid id, [FromBody] SetRolesRequest request, CancellationToken cancellationToken)
    {
        var outcome = await adminAccounts.SetRolesAsync(
            id, await CurrentAccountIdAsync(), request.Roles, ClientIpAddress, cancellationToken);
        return ToResult(outcome, invalidRolesCode: "admin.invalid_roles", selfCode: "admin.cannot_remove_own_admin_role");
    }

    [HttpPost("{id:guid}/lock")]
    [RequireStepUp]
    [EnableRateLimiting("auth")]
    public async Task<IResult> Lock(Guid id, [FromBody] LockAccountRequest request, CancellationToken cancellationToken)
    {
        var until = request.Until ?? DateTimeOffset.MaxValue;
        var outcome = await adminAccounts.LockAsync(id, await CurrentAccountIdAsync(), until, ClientIpAddress, cancellationToken);
        return ToResult(outcome, selfCode: "admin.cannot_lock_own_account");
    }

    [HttpPost("{id:guid}/unlock")]
    [RequireStepUp]
    [EnableRateLimiting("auth")]
    public async Task<IResult> Unlock(Guid id, CancellationToken cancellationToken)
    {
        var outcome = await adminAccounts.UnlockAsync(id, await CurrentAccountIdAsync(), ClientIpAddress, cancellationToken);
        return ToResult(outcome);
    }

    [HttpPost("{id:guid}/revoke-sessions")]
    [RequireStepUp]
    [EnableRateLimiting("auth")]
    public async Task<IResult> RevokeSessions(Guid id, CancellationToken cancellationToken)
    {
        var outcome = await adminAccounts.RevokeSessionsAsync(id, await CurrentAccountIdAsync(), ClientIpAddress, cancellationToken);
        return ToResult(outcome);
    }

    private async Task<Guid> CurrentAccountIdAsync() =>
        (await userManager.GetUserAsync(User) ?? throw new InvalidOperationException("Signed-in user not found.")).Id;

    private static IResult ToResult(AdminAccountService.Outcome outcome, string? invalidRolesCode = null, string? selfCode = null) =>
        outcome switch
        {
            AdminAccountService.Outcome.Success => Results.NoContent(),
            AdminAccountService.Outcome.NotFound => Results.NotFound(),
            AdminAccountService.Outcome.InvalidRoles => new ErrorResult(
                Error.Validation(invalidRolesCode!, "Unknown role name", "roles")),
            AdminAccountService.Outcome.CannotTargetSelf => new ErrorResult(
                Error.Validation(selfCode!, "You cannot perform this action on your own account", "id")),
            _ => throw new InvalidOperationException($"Unhandled outcome: {outcome}"),
        };
}
