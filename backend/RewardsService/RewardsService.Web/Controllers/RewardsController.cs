using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using CSharpFunctionalExtensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using RewardsService.Domain;
using RewardsService.Infrastructure;
using RewardsService.Web.Rewards;
using Shared;
using Shared.Database;
using Shared.EndpointResults;
using Shared.Security;

namespace RewardsService.Web.Controllers;

public static class AgentGrantLimits
{
    // An assistant can be talked into things; this bounds what one grant can cost. McpServer keeps a copy of the
    // number only to refuse early with a readable message - this is the one that holds.
    public const decimal MaxPerGrant = 500;

    // Per granting account per UTC day. The ledger enforces it, not the assistant.
    public const decimal MaxPerDay = 2000;
}

public record GrantCurrencyRequest(Guid EmployeeId, decimal Amount, string Reason);

public record WalletDto(Guid EmployeeId, decimal Balance);

[ApiController]
[Route("api/rewards")]
[Authorize]
public class RewardsController(RewardsDbContext dbContext, CurrencyGrantWriter writer) : ControllerBase
{
    private const string ManualScope = "rewards.grants";
    private const string AgentScope = "rewards.agent-grants";

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

        Result<Guid, Error> result = HandleGrant(idempotencyKey, request, ManualScope, TransactionSource.ManualGrant);
        return result;
    }

    // What an assistant may do on someone's behalf: the same grant, but a separate route, so its rules can be
    // stricter than a person's without changing the manual contract, and its ledger rows say where they came from.
    [HttpPost("agent-grants")]
    [RequireCanEdit]
    [EnableRateLimiting("write")]
    public EndpointResult<Guid> AgentGrant(
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, GrantCurrencyRequest request)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return Result.Failure<Guid, Error>(RewardsErrors.IdempotencyKeyRequired());
        }

        Result<Guid, Error> result = HandleGrant(idempotencyKey, request, AgentScope, TransactionSource.AgentGrant);
        return result;
    }

    private Result<Guid, Error> HandleGrant(
        string idempotencyKey, GrantCurrencyRequest request, string idempotencyScope, TransactionSource source)
    {
        if (request.Amount <= 0)
        {
            return RewardsErrors.AmountMustBePositive();
        }

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return RewardsErrors.ReasonRequired();
        }

        if (source == TransactionSource.AgentGrant && request.Amount > AgentGrantLimits.MaxPerGrant)
        {
            return RewardsErrors.AgentGrantOverLimit(AgentGrantLimits.MaxPerGrant);
        }

        // "sub" is an account, wallets are keyed by employee: the AccountProvisioned projection links them. An
        // account with no linked employee cannot be granting to itself, so the rule then does not apply.
        var callerAccount = Guid.Parse(User.FindFirstValue("sub")!);
        if (dbContext.AccountLookups.Any(l => l.AccountId == callerAccount && l.EmployeeId == request.EmployeeId))
        {
            return RewardsErrors.CannotGrantToSelf();
        }

        var requestHash = HashRequest(request);

        var existing = dbContext.IdempotencyRecords
            .SingleOrDefault(r => r.Scope == idempotencyScope && r.Key == idempotencyKey);
        if (existing is not null)
        {
            return existing.RequestHash == requestHash
                ? existing.TransactionId
                : RewardsErrors.IdempotencyKeyReused();
        }

        var grantedBy = Guid.Parse(User.FindFirstValue("sub")!);

        // The quota moves in the same transaction as the ledger write and is committed only with it, so a request
        // that loses the idempotency race below never spends it.
        using var quotaTransaction = source == TransactionSource.AgentGrant ? dbContext.Database.BeginTransaction() : null;
        if (quotaTransaction is not null && !TryUseDailyQuota(grantedBy, request.Amount))
        {
            return RewardsErrors.AgentQuotaExceeded(AgentGrantLimits.MaxPerDay);
        }

        var transaction = writer.Grant(
            request.EmployeeId, request.Amount, request.Reason, source, grantedBy);

        dbContext.IdempotencyRecords.Add(
            IdempotencyRecord.Create(idempotencyScope, idempotencyKey, requestHash, transaction.Id));

        try
        {
            dbContext.SaveChanges();
            quotaTransaction?.Commit();
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            // Not committed, so the quota this loser had taken is undone when the transaction is disposed.

            // Raced another request on the same key - wallet update, transaction, outbox
            // message and this row are all one SaveChanges call, so the loser's writes never
            // partially committed. Re-read and return the winner's result instead of retrying
            // the grant itself.
            dbContext.ChangeTracker.Clear();
            var winner = dbContext.IdempotencyRecords
                .Single(r => r.Scope == idempotencyScope && r.Key == idempotencyKey);
            return winner.RequestHash == requestHash
                ? winner.TransactionId
                : RewardsErrors.IdempotencyKeyReused();
        }

        return transaction.Id;
    }

    // One statement, so there is no gap between "how much is used" and "add mine": a first use inserts the row, a
    // later one adds to it only while the total stays within the limit. Zero rows changed means it would not fit.
    // A first insert cannot exceed the limit because a single grant is already capped well below it.
    private bool TryUseDailyQuota(Guid account, decimal amount)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var max = AgentGrantLimits.MaxPerDay;
        var changed = dbContext.Database.ExecuteSqlInterpolated($"""
            INSERT INTO rewards.agent_grant_usage ("GrantedByAccountId", "Day", "Used")
            VALUES ({account}, {today}, {amount})
            ON CONFLICT ("GrantedByAccountId", "Day")
            DO UPDATE SET "Used" = rewards.agent_grant_usage."Used" + EXCLUDED."Used"
            WHERE rewards.agent_grant_usage."Used" + EXCLUDED."Used" <= {max}
            """);
        return changed == 1;
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
