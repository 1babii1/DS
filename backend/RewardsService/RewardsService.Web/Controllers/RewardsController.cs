using System.Security.Claims;
using CSharpFunctionalExtensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using RewardsService.Domain;
using RewardsService.Infrastructure;
using RewardsService.Web.Rewards;
using Shared;
using Shared.EndpointResults;

namespace RewardsService.Web.Controllers;

public record GrantCurrencyRequest(Guid EmployeeId, decimal Amount, string Reason);

public record WalletDto(Guid EmployeeId, decimal Balance);

[ApiController]
[Route("api/rewards")]
[Authorize]
public class RewardsController(RewardsDbContext dbContext, CurrencyGrantWriter writer) : ControllerBase
{
    // Only manual grants go through here - not synchronously validated against
    // EmployeeService that EmployeeId actually exists (see the Rewards ledger ADR):
    // a wrong id just means a wallet nobody ever reads gets created, not a corrupted one.
    [HttpPost("grants")]
    [Authorize(Policy = "CanEdit")]
    [EnableRateLimiting("write")]
    public EndpointResult<Guid> Grant(GrantCurrencyRequest request)
    {
        Result<Guid, Error> result = HandleGrant(request);
        return result;
    }

    private Result<Guid, Error> HandleGrant(GrantCurrencyRequest request)
    {
        if (request.Amount <= 0)
        {
            return RewardsErrors.AmountMustBePositive();
        }

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return RewardsErrors.ReasonRequired();
        }

        var grantedBy = Guid.Parse(User.FindFirstValue("sub")!);
        var transaction = writer.Grant(
            request.EmployeeId, request.Amount, request.Reason, TransactionSource.ManualGrant, grantedBy);

        dbContext.SaveChanges();

        return transaction.Id;
    }

    // Caller's own wallet, from the JWT - never a request parameter. "sub" is the caller's
    // AccountId, which is NOT the EmployeeId wallets are keyed by, so it has to be resolved
    // through the AccountProvisioned projection first; reading the wallet by "sub" directly
    // silently matched nothing and reported a zero balance to everyone.
    [HttpGet("wallet")]
    public async Task<ActionResult<WalletDto>> GetOwnWallet(CancellationToken cancellationToken)
    {
        var accountId = Guid.Parse(User.FindFirstValue("sub")!);
        var employeeId = await dbContext.AccountLookups.AsNoTracking()
            .Where(l => l.AccountId == accountId)
            .Select(l => (Guid?)l.EmployeeId)
            .SingleOrDefaultAsync(cancellationToken);

        // No mapping means this account was never provisioned from a hire (a self-registered
        // account, or one created before provisioning existed) - it owns no wallet, which is
        // the same zero-balance answer as an employee who has never been granted anything.
        if (employeeId is null)
        {
            return Ok(new WalletDto(accountId, 0));
        }

        return await GetWallet(employeeId.Value, cancellationToken);
    }

    [HttpGet("wallet/{employeeId:guid}")]
    [Authorize(Policy = "CanEdit")]
    public async Task<ActionResult<WalletDto>> GetWalletFor(
        [FromRoute] Guid employeeId, CancellationToken cancellationToken) =>
        await GetWallet(employeeId, cancellationToken);

    private async Task<ActionResult<WalletDto>> GetWallet(Guid employeeId, CancellationToken cancellationToken)
    {
        var wallet = await dbContext.Wallets.AsNoTracking()
            .SingleOrDefaultAsync(w => w.EmployeeId == employeeId, cancellationToken);

        // No wallet yet just means no currency has ever moved for this employee -
        // a zero balance, not a 404. A wallet row is only created on first grant.
        return Ok(new WalletDto(employeeId, wallet?.Balance ?? 0));
    }
}
