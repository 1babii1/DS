using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using CSharpFunctionalExtensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using RewardsService.Domain;
using RewardsService.Infrastructure;
using RewardsService.Web.Rewards;
using Shared;
using Shared.Database;
using Shared.EndpointResults;
using Shared.Security;

namespace RewardsService.Web.Controllers;

public record GrantCurrencyRequest(Guid EmployeeId, decimal Amount, string Reason);

public record WalletDto(Guid EmployeeId, decimal Balance);

[ApiController]
[Route("api/rewards")]
[Authorize]
public class RewardsController(RewardsDbContext dbContext, CurrencyGrantWriter writer) : ControllerBase
{
    private const string IdempotencyScope = "rewards.grants";

    // Only manual grants go through here - not synchronously validated against
    // EmployeeService that EmployeeId actually exists (see the Rewards ledger ADR):
    // a wrong id just means a wallet nobody ever reads gets created, not a corrupted one.
    //
    // Idempotency-Key is required, not optional: this endpoint has no natural dedup key of
    // its own (unlike hire, which rejects a repeat by its unique Email index), so without
    // one a client retry after a timeout - or a double-click - is a second grant.
    [HttpPost("grants")]
    [RequireCanEdit]
    [EnableRateLimiting("write")]
    public EndpointResult<Guid> Grant(
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, GrantCurrencyRequest request)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return Result.Failure<Guid, Error>(RewardsErrors.IdempotencyKeyRequired());
        }

        Result<Guid, Error> result = HandleGrant(idempotencyKey, request);
        return result;
    }

    private Result<Guid, Error> HandleGrant(string idempotencyKey, GrantCurrencyRequest request)
    {
        if (request.Amount <= 0)
        {
            return RewardsErrors.AmountMustBePositive();
        }

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return RewardsErrors.ReasonRequired();
        }

        var requestHash = HashRequest(request);

        var existing = dbContext.IdempotencyRecords
            .SingleOrDefault(r => r.Scope == IdempotencyScope && r.Key == idempotencyKey);
        if (existing is not null)
        {
            return existing.RequestHash == requestHash
                ? existing.TransactionId
                : RewardsErrors.IdempotencyKeyReused();
        }

        var grantedBy = Guid.Parse(User.FindFirstValue("sub")!);
        var transaction = writer.Grant(
            request.EmployeeId, request.Amount, request.Reason, TransactionSource.ManualGrant, grantedBy);

        dbContext.IdempotencyRecords.Add(
            IdempotencyRecord.Create(IdempotencyScope, idempotencyKey, requestHash, transaction.Id));

        try
        {
            dbContext.SaveChanges();
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            // Raced another request on the same key - wallet update, transaction, outbox
            // message and this row are all one SaveChanges call, so the loser's writes never
            // partially committed. Re-read and return the winner's result instead of retrying
            // the grant itself.
            dbContext.ChangeTracker.Clear();
            var winner = dbContext.IdempotencyRecords
                .Single(r => r.Scope == IdempotencyScope && r.Key == idempotencyKey);
            return winner.RequestHash == requestHash
                ? winner.TransactionId
                : RewardsErrors.IdempotencyKeyReused();
        }

        return transaction.Id;
    }

    private static string HashRequest(GrantCurrencyRequest request)
    {
        var canonical = $"{request.EmployeeId:D}|{request.Amount}|{request.Reason}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(hash);
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
    [RequireCanEdit]
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
