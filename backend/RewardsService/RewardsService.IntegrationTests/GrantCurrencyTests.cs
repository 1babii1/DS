using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RewardsService.Domain;
using RewardsService.Infrastructure;
using RewardsService.Web.Controllers;

namespace RewardsService.IntegrationTests;

// The manual grant path. Currency is the one thing in this platform with ledger semantics,
// so what is asserted here is that a grant is never partially applied: wallet balance,
// ledger row and outbox message either all exist or none do.
public class GrantCurrencyTests : IClassFixture<RewardsTestWebFactory>, IAsyncLifetime
{
    private readonly Func<Task> _resetDatabase;
    private readonly IServiceProvider _services;

    public GrantCurrencyTests(RewardsTestWebFactory factory)
    {
        _services = factory.Services;
        _resetDatabase = factory.ResetDatabaseAsync;
    }

    [Fact]
    public async Task A_grant_writes_wallet_ledger_and_outbox_together()
    {
        var employeeId = Guid.NewGuid();
        var grantedBy = Guid.NewGuid();

        var (status, _) = await Grant(grantedBy, new GrantCurrencyRequest(employeeId, 250, "Spot bonus"));

        Assert.Equal(StatusCodes.Status200OK, status);

        var wallet = await ReadInDb(db => db.Wallets.SingleAsync(w => w.EmployeeId == employeeId));
        Assert.Equal(250, wallet.Balance);

        var transaction = await ReadInDb(db => db.Transactions.SingleAsync(t => t.EmployeeId == employeeId));
        Assert.Equal(250, transaction.Amount);
        Assert.Equal("Spot bonus", transaction.Reason);
        Assert.Equal(TransactionSource.ManualGrant, transaction.Source);

        // Who granted it is taken from the caller's token, never from the request body.
        Assert.Equal(grantedBy, transaction.GrantedByAccountId);

        Assert.Equal(1, await ReadInDb(db => db.OutboxMessages.CountAsync()));
    }

    [Fact]
    public async Task Repeated_grants_accumulate_on_one_wallet()
    {
        var employeeId = Guid.NewGuid();

        await Grant(Guid.NewGuid(), new GrantCurrencyRequest(employeeId, 100, "First"));
        await Grant(Guid.NewGuid(), new GrantCurrencyRequest(employeeId, 50, "Second"));

        var wallet = await ReadInDb(db => db.Wallets.SingleAsync(w => w.EmployeeId == employeeId));
        Assert.Equal(150, wallet.Balance);
        Assert.Equal(2, await ReadInDb(db => db.Transactions.CountAsync(t => t.EmployeeId == employeeId)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-9999.99)]
    public async Task A_non_positive_amount_is_rejected(decimal amount)
    {
        var employeeId = Guid.NewGuid();

        var (status, errorCode) = await Grant(Guid.NewGuid(), new GrantCurrencyRequest(employeeId, amount, "Nope"));

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Equal("rewards.amount.must_be_positive", errorCode);
        await AssertNothingWasWritten();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_reason_is_rejected(string reason)
    {
        var (status, errorCode) = await Grant(Guid.NewGuid(), new GrantCurrencyRequest(Guid.NewGuid(), 100, reason));

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Equal("rewards.reason.required", errorCode);
        await AssertNothingWasWritten();
    }

    [Fact]
    public async Task A_missing_idempotency_key_is_rejected()
    {
        var (status, errorCode) = await Grant(
            Guid.NewGuid(), new GrantCurrencyRequest(Guid.NewGuid(), 100, "Bonus"), idempotencyKey: null);

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Equal("rewards.idempotency_key.required", errorCode);
        await AssertNothingWasWritten();
    }

    [Fact]
    public async Task Replaying_the_same_idempotency_key_returns_the_original_grant_without_a_second_one()
    {
        var employeeId = Guid.NewGuid();
        var request = new GrantCurrencyRequest(employeeId, 250, "Spot bonus");
        var key = Guid.NewGuid().ToString();

        var (firstStatus, firstTransactionId) = await GrantReturningTransactionId(Guid.NewGuid(), request, key);
        var (secondStatus, secondTransactionId) = await GrantReturningTransactionId(Guid.NewGuid(), request, key);

        Assert.Equal(StatusCodes.Status200OK, firstStatus);
        Assert.Equal(StatusCodes.Status200OK, secondStatus);
        Assert.Equal(firstTransactionId, secondTransactionId);

        // One grant, not two - the retry returned the original result instead of applying
        // the amount again.
        var wallet = await ReadInDb(db => db.Wallets.SingleAsync(w => w.EmployeeId == employeeId));
        Assert.Equal(250, wallet.Balance);
        Assert.Equal(1, await ReadInDb(db => db.Transactions.CountAsync(t => t.EmployeeId == employeeId)));
        Assert.Equal(1, await ReadInDb(db => db.OutboxMessages.CountAsync()));
    }

    [Fact]
    public async Task Reusing_an_idempotency_key_with_a_different_body_is_rejected()
    {
        var key = Guid.NewGuid().ToString();

        await Grant(Guid.NewGuid(), new GrantCurrencyRequest(Guid.NewGuid(), 100, "First"), key);
        var (status, errorCode) = await Grant(
            Guid.NewGuid(), new GrantCurrencyRequest(Guid.NewGuid(), 999, "Different"), key);

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, status);
        Assert.Equal("rewards.idempotency_key.reused", errorCode);

        // Only the first grant exists - the rejected replay wrote nothing.
        Assert.Equal(1, await ReadInDb(db => db.Transactions.CountAsync()));
    }

    [Fact]
    public async Task Concurrent_requests_with_the_same_idempotency_key_apply_exactly_one_grant()
    {
        var employeeId = Guid.NewGuid();
        var request = new GrantCurrencyRequest(employeeId, 250, "Spot bonus");
        var key = Guid.NewGuid().ToString();

        // Real contention, not a sequential simulation: every task starts from the same
        // barrier so the unique (Scope, Key) index actually gets raced, the same technique
        // WelcomeBonusConsumerTests uses to reproduce the equivalent race on that path.
        using var barrier = new Barrier(8);
        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            barrier.SignalAndWait();
            return GrantReturningTransactionId(Guid.NewGuid(), request, key);
        }));

        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Equal(StatusCodes.Status200OK, r.Status));
        Assert.Single(results.Select(r => r.TransactionId).Distinct());

        var wallet = await ReadInDb(db => db.Wallets.SingleAsync(w => w.EmployeeId == employeeId));
        Assert.Equal(250, wallet.Balance);
        Assert.Equal(1, await ReadInDb(db => db.Transactions.CountAsync(t => t.EmployeeId == employeeId)));
    }

    [Fact]
    public async Task A_wallet_that_was_never_granted_anything_reads_as_zero_rather_than_404()
    {
        var employeeId = Guid.NewGuid();

        var response = await ReadWalletFor(employeeId);

        var wallet = Assert.IsType<WalletDto>(Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(0, wallet.Balance);
        Assert.Equal(employeeId, wallet.EmployeeId);
    }

    // Nobody grants themselves currency. The caller's identity is an account, the wallet is keyed by employee;
    // the projection of AccountProvisioned links the two. A second, unrelated employee is the control: the same
    // caller can still grant to them, so the refusal is about "me", not about granting in general.
    [Fact]
    public async Task A_grant_to_the_callers_own_employee_is_refused_and_writes_nothing()
    {
        var callerAccount = Guid.NewGuid();
        var callerEmployee = Guid.NewGuid();
        var colleague = Guid.NewGuid();
        await SeedAccountLink(callerEmployee, callerAccount);

        var (ownStatus, ownCode) = await Grant(callerAccount, new GrantCurrencyRequest(callerEmployee, 100, "Me"));
        var (otherStatus, _) = await Grant(callerAccount, new GrantCurrencyRequest(colleague, 100, "Them"));

        Assert.Equal(StatusCodes.Status403Forbidden, ownStatus);
        Assert.Equal("rewards.grant.self", ownCode);
        Assert.Equal(0, await ReadInDb(db => db.Transactions.CountAsync(t => t.EmployeeId == callerEmployee)));
        Assert.Equal(StatusCodes.Status200OK, otherStatus);
    }

    [Fact]
    public async Task An_account_with_no_linked_employee_is_not_affected_by_the_self_grant_rule()
    {
        var (status, _) = await Grant(Guid.NewGuid(), new GrantCurrencyRequest(Guid.NewGuid(), 100, "Fine"));

        Assert.Equal(StatusCodes.Status200OK, status);
    }

    // Agent grants are a separate, stricter route: a small ceiling per grant and their own ledger source, so an
    // assistant that has been talked into something has a bounded reach. The manual route keeps its contract.
    [Fact]
    public async Task An_agent_grant_within_the_ceiling_is_recorded_with_its_own_source()
    {
        var employeeId = Guid.NewGuid();
        var grantedBy = Guid.NewGuid();

        var (status, _) = await Grant(grantedBy, new GrantCurrencyRequest(employeeId, 500, "Release"), agent: true);

        Assert.Equal(StatusCodes.Status200OK, status);
        var transaction = await ReadInDb(db => db.Transactions.SingleAsync(t => t.EmployeeId == employeeId));
        Assert.Equal(TransactionSource.AgentGrant, transaction.Source);
        Assert.Equal(grantedBy, transaction.GrantedByAccountId);
    }

    [Fact]
    public async Task An_agent_grant_above_the_ceiling_is_refused_and_writes_nothing_while_the_manual_route_still_allows_it()
    {
        var employeeId = Guid.NewGuid();

        var (agentStatus, agentCode) = await Grant(
            Guid.NewGuid(), new GrantCurrencyRequest(employeeId, 501, "Too much"), agent: true);
        var (manualStatus, _) = await Grant(Guid.NewGuid(), new GrantCurrencyRequest(employeeId, 501, "Manual"));

        Assert.Equal(StatusCodes.Status400BadRequest, agentStatus);
        Assert.Equal("rewards.agent_grant.over_limit", agentCode);
        Assert.Equal(StatusCodes.Status200OK, manualStatus);
        Assert.Equal(1, await ReadInDb(db => db.Transactions.CountAsync(t => t.EmployeeId == employeeId)));
    }

    [Fact]
    public async Task An_agent_grant_to_the_callers_own_employee_is_refused()
    {
        var callerAccount = Guid.NewGuid();
        var callerEmployee = Guid.NewGuid();
        await SeedAccountLink(callerEmployee, callerAccount);

        var (status, code) = await Grant(
            callerAccount, new GrantCurrencyRequest(callerEmployee, 100, "Me"), agent: true);

        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.Equal("rewards.grant.self", code);
    }

    [Fact]
    public async Task An_agent_grant_repeated_with_the_same_key_returns_the_original_and_grants_once()
    {
        var employeeId = Guid.NewGuid();
        var request = new GrantCurrencyRequest(employeeId, 100, "Once");

        var first = await GrantReturningTransactionId(Guid.NewGuid(), request, "agent-key", agent: true);
        var second = await GrantReturningTransactionId(Guid.NewGuid(), request, "agent-key", agent: true);

        Assert.Equal(first.TransactionId, second.TransactionId);
        Assert.Equal(1, await ReadInDb(db => db.Transactions.CountAsync(t => t.EmployeeId == employeeId)));
    }

    // The daily limit (2000 per granting account) lives in the ledger, not in the assistant.
    [Fact]
    public async Task Agent_grants_stop_at_the_daily_limit_for_that_account_only()
    {
        var account = Guid.NewGuid();
        var other = Guid.NewGuid();
        for (var i = 0; i < 4; i++)
        {
            var (ok, _) = await Grant(account, new GrantCurrencyRequest(Guid.NewGuid(), 500, "Spend"), agent: true);
            Assert.Equal(StatusCodes.Status200OK, ok);
        }

        var (overStatus, overCode) = await Grant(
            account, new GrantCurrencyRequest(Guid.NewGuid(), 1, "One more"), agent: true);
        var (otherStatus, _) = await Grant(other, new GrantCurrencyRequest(Guid.NewGuid(), 500, "Fresh"), agent: true);
        var (manualStatus, _) = await Grant(account, new GrantCurrencyRequest(Guid.NewGuid(), 500, "By hand"));

        Assert.Equal(StatusCodes.Status400BadRequest, overStatus);
        Assert.Equal("rewards.agent_grant.over_quota", overCode);
        Assert.Equal(StatusCodes.Status200OK, otherStatus);
        Assert.Equal(StatusCodes.Status200OK, manualStatus);
    }

    [Fact]
    public async Task Yesterdays_usage_does_not_count_against_today()
    {
        var account = Guid.NewGuid();
        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RewardsDbContext>();
            db.AgentGrantUsages.Add(AgentGrantUsage.Create(account, yesterday, 2000));
            await db.SaveChangesAsync();
        }

        var (status, _) = await Grant(account, new GrantCurrencyRequest(Guid.NewGuid(), 500, "Today"), agent: true);

        Assert.Equal(StatusCodes.Status200OK, status);
    }

    // The limit is a sum, so it is only meaningful against an account that has already used part of it: a fresh
    // account passes a check-then-write version by accident. 1500 is used first, then two 400s arrive together;
    // only one fits, and the other must be refused rather than both reading 1500 and both passing.
    [Fact]
    public async Task Two_simultaneous_agent_grants_cannot_both_pass_the_remaining_quota()
    {
        var account = Guid.NewGuid();
        for (var i = 0; i < 3; i++)
        {
            await Grant(account, new GrantCurrencyRequest(Guid.NewGuid(), 500, "Used"), agent: true);
        }

        var results = await Concurrently(
            () => Grant(account, new GrantCurrencyRequest(Guid.NewGuid(), 400, "A"), agent: true),
            () => Grant(account, new GrantCurrencyRequest(Guid.NewGuid(), 400, "B"), agent: true));

        Assert.Single(results, r => r.Status == StatusCodes.Status200OK);
        Assert.Single(results, r => r.ErrorCode == "rewards.agent_grant.over_quota");
        var used = await ReadInDb(db => db.AgentGrantUsages.SingleAsync(u => u.GrantedByAccountId == account));
        Assert.Equal(1900, used.Used);
    }

    // Several requests with the same key at once: one wins the idempotency race, the others must not keep the quota
    // they had already taken: the count is 500, not 500 times the number of requests.
    [Fact]
    public async Task Many_simultaneous_agent_grants_with_one_key_use_the_quota_once()
    {
        var account = Guid.NewGuid();
        var request = new GrantCurrencyRequest(Guid.NewGuid(), 500, "Once");

        // Eight, not two: the losing path only runs when a request passes the "already granted?" read before
        // another has committed, and two requests very often do not overlap that closely.
        var results = await Concurrently(Enumerable.Range(0, 8)
            .Select<int, Func<Task<(int Status, string? ErrorCode)>>>(_ => () => Grant(account, request, "shared-key", agent: true))
            .ToArray());

        Assert.All(results, r => Assert.Equal(StatusCodes.Status200OK, r.Status));
        var used = await ReadInDb(db => db.AgentGrantUsages.SingleAsync(u => u.GrantedByAccountId == account));
        Assert.Equal(500, used.Used);
        Assert.Equal(1, await ReadInDb(db => db.Transactions.CountAsync(t => t.EmployeeId == request.EmployeeId)));
    }

    [Fact]
    public async Task Repeating_an_agent_grant_with_the_same_key_does_not_use_the_quota_twice()
    {
        var account = Guid.NewGuid();
        var request = new GrantCurrencyRequest(Guid.NewGuid(), 500, "Once");
        await GrantReturningTransactionId(account, request, "same-key", agent: true);
        await GrantReturningTransactionId(account, request, "same-key", agent: true);

        var used = await ReadInDb(db => db.AgentGrantUsages.SingleAsync(u => u.GrantedByAccountId == account));

        Assert.Equal(500, used.Used);
    }

    // The controller body is synchronous, so Task.WhenAll over two calls would run them one after the other. Each call
    // goes to its own thread and all start together, so the requests genuinely overlap in the database.
    private static async Task<(int Status, string? ErrorCode)[]> Concurrently(
        params Func<Task<(int Status, string? ErrorCode)>>[] calls)
    {
        using var gate = new ManualResetEventSlim(false);
        var running = calls.Select(call => Task.Run(() =>
        {
            gate.Wait();
            return call();
        })).ToArray();
        await Task.Delay(100);
        gate.Set();
        return await Task.WhenAll(running);
    }

    private async Task SeedAccountLink(Guid employeeId, Guid accountId)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RewardsDbContext>();
        db.AccountLookups.Add(AccountLookup.Create(employeeId, accountId));
        await db.SaveChangesAsync();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _resetDatabase();

    private async Task AssertNothingWasWritten()
    {
        Assert.Equal(0, await ReadInDb(db => db.Wallets.CountAsync()));
        Assert.Equal(0, await ReadInDb(db => db.Transactions.CountAsync()));
        Assert.Equal(0, await ReadInDb(db => db.OutboxMessages.CountAsync()));
    }

    private async Task<T> ReadInDb<T>(Func<RewardsDbContext, Task<T>> read)
    {
        await using var scope = _services.CreateAsyncScope();
        return await read(scope.ServiceProvider.GetRequiredService<RewardsDbContext>());
    }

    // EndpointResult deliberately exposes nothing but IResult, so the assertion runs the
    // real HTTP contract: execute the result and read back status plus the error envelope.
    // idempotencyKey defaults to a fresh key per call - every existing test that doesn't
    // care about idempotency still gets a valid request instead of hitting the new
    // required-header rejection.
    private async Task<(int Status, string? ErrorCode)> Grant(
        Guid grantedBy, GrantCurrencyRequest request, string? idempotencyKey = "default", bool agent = false)
    {
        var (status, errorCode, _) = await GrantInternal(
            grantedBy, request, idempotencyKey == "default" ? Guid.NewGuid().ToString() : idempotencyKey, agent);
        return (status, errorCode);
    }

    private async Task<(int Status, Guid? TransactionId)> GrantReturningTransactionId(
        Guid grantedBy, GrantCurrencyRequest request, string idempotencyKey, bool agent = false)
    {
        var (status, _, transactionId) = await GrantInternal(grantedBy, request, idempotencyKey, agent);
        return (status, transactionId);
    }

    private async Task<(int Status, string? ErrorCode, Guid? TransactionId)> GrantInternal(
        Guid grantedBy, GrantCurrencyRequest request, string? idempotencyKey, bool agent = false)
    {
        await using var scope = _services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RewardsDbContext>();

        var httpContext = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            Response = { Body = new MemoryStream() },
        };

        var controller = Controller(dbContext, grantedBy);
        await (agent ? controller.AgentGrant(idempotencyKey, request) : controller.Grant(idempotencyKey, request))
            .ExecuteAsync(httpContext);

        httpContext.Response.Body.Seek(0, SeekOrigin.Begin);
        using var document = await JsonDocument.ParseAsync(httpContext.Response.Body);

        string? errorCode = null;
        Guid? transactionId = null;
        if (document.RootElement.TryGetProperty("error", out var error) &&
            error.ValueKind is JsonValueKind.Object &&
            error.TryGetProperty("messages", out var messages) &&
            messages.GetArrayLength() > 0)
        {
            errorCode = messages[0].GetProperty("code").GetString();
        }
        else if (document.RootElement.TryGetProperty("result", out var result) &&
            result.ValueKind is JsonValueKind.String)
        {
            transactionId = result.GetGuid();
        }

        return (httpContext.Response.StatusCode, errorCode, transactionId);
    }

    private async Task<ActionResult<WalletDto>> ReadWalletFor(Guid employeeId)
    {
        await using var scope = _services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RewardsDbContext>();
        return await Controller(dbContext, Guid.NewGuid()).GetWalletFor(employeeId, CancellationToken.None);
    }

    private static RewardsController Controller(RewardsDbContext dbContext, Guid callerAccountId) =>
        new(dbContext, new CurrencyGrantWriter(dbContext))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(
                        new ClaimsIdentity([new Claim("sub", callerAccountId.ToString())], "test")),
                },
            },
        };
}
