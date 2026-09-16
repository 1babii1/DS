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

    // Caller's own wallet, from the JWT - never a request parameter.
    [HttpGet("wallet")]
    public async Task<ActionResult<WalletDto>> GetOwnWallet(CancellationToken cancellationToken)
    {
        var employeeId = Guid.Parse(User.FindFirstValue("sub")!);
        return await GetWallet(employeeId, cancellationToken);
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
