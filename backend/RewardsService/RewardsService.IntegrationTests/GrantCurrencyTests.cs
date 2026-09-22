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
        Assert.Equal(1, results.Select(r => r.TransactionId).Distinct().Count());

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
        Guid grantedBy, GrantCurrencyRequest request, string? idempotencyKey = "default")
    {
        var (status, errorCode, _) = await GrantInternal(
            grantedBy, request, idempotencyKey == "default" ? Guid.NewGuid().ToString() : idempotencyKey);
        return (status, errorCode);
    }

    private async Task<(int Status, Guid? TransactionId)> GrantReturningTransactionId(
        Guid grantedBy, GrantCurrencyRequest request, string idempotencyKey)
    {
        var (status, _, transactionId) = await GrantInternal(grantedBy, request, idempotencyKey);
        return (status, transactionId);
    }

    private async Task<(int Status, string? ErrorCode, Guid? TransactionId)> GrantInternal(
        Guid grantedBy, GrantCurrencyRequest request, string? idempotencyKey)
    {
        await using var scope = _services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RewardsDbContext>();

        var httpContext = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            Response = { Body = new MemoryStream() },
        };

        await Controller(dbContext, grantedBy).Grant(idempotencyKey, request).ExecuteAsync(httpContext);

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
