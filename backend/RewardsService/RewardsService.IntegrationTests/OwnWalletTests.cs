using System.Security.Claims;
using System.Text;
using Confluent.Kafka;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RewardsService.Infrastructure;
using RewardsService.Infrastructure.Consumers;
using RewardsService.Web.Controllers;

namespace RewardsService.IntegrationTests;

// The wallet a caller owns is keyed by EmployeeId, but their JWT only carries AccountId in
// "sub". These two identifiers are unrelated, so resolving one to the other is the entire
// correctness question for this endpoint - and it previously had no test at all, which is
// why reading the wallet by "sub" directly went unnoticed while always returning 0.
public class OwnWalletTests : IClassFixture<RewardsTestWebFactory>, IAsyncLifetime
{
    private readonly Func<Task> _resetDatabase;
    private readonly IServiceProvider _services;
    private readonly WelcomeBonusConsumer _consumer;

    public OwnWalletTests(RewardsTestWebFactory factory)
    {
        _services = factory.Services;
        _resetDatabase = factory.ResetDatabaseAsync;

        _consumer = new WelcomeBonusConsumer(
            _services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new WelcomeBonusConsumerOptions { WelcomeBonusAmount = 100 }),
            _services.GetRequiredService<ILogger<WelcomeBonusConsumer>>());
    }

    [Fact]
    public async Task Own_wallet_resolves_the_caller_account_to_their_employee_wallet()
    {
        var employeeId = Guid.NewGuid();
        var accountId = Guid.NewGuid();

        Assert.True(_consumer.HandleWithRetryAndDeadLetter(Hired(employeeId), CancellationToken.None));
        Assert.True(_consumer.HandleWithRetryAndDeadLetter(Provisioned(employeeId, accountId), CancellationToken.None));

        var wallet = await ReadOwnWallet(accountId);

        Assert.Equal(employeeId, wallet.EmployeeId);
        Assert.Equal(100, wallet.Balance);
    }

    [Fact]
    public async Task Own_wallet_is_zero_for_an_account_that_was_never_provisioned_from_a_hire()
    {
        var accountId = Guid.NewGuid();

        var wallet = await ReadOwnWallet(accountId);

        Assert.Equal(0, wallet.Balance);
    }

    // The gap this documents: EmployeeHired and AccountProvisioned are two independent
    // events on two different topics, consumed independently by this same consumer. The
    // welcome bonus is granted purely from EmployeeHired - it does not wait for
    // AccountProvisioned - so there is a real window, between the two being consumed,
    // where the Transaction/Wallet already exist but AccountLookup does not yet. A caller
    // hitting GET /api/rewards/wallet in that window sees 0, indistinguishable from "no
    // bonus was ever granted", even though the money is already in their ledger. Same
    // documented ordering caveat as ADR 0006's ("a notification that outraces
    // AccountProvisioned is silently skipped"), just not previously written down for this
    // endpoint. It self-resolves the moment AccountProvisioned is consumed - nothing here
    // is data loss - but "self-resolves eventually" and "looks broken right now" are both
    // true at once, which is exactly the ambiguity worth a named test.
    [Fact]
    public async Task Own_wallet_reads_as_zero_between_the_bonus_being_granted_and_the_account_being_linked()
    {
        var employeeId = Guid.NewGuid();
        var accountId = Guid.NewGuid();

        // Only EmployeeHired consumed so far - AccountProvisioned has not arrived yet.
        Assert.True(_consumer.HandleWithRetryAndDeadLetter(Hired(employeeId), CancellationToken.None));

        var walletBeforeLinking = await ReadOwnWallet(accountId);
        Assert.Equal(0, walletBeforeLinking.Balance);

        // The bonus was, in fact, already granted - just not reachable by AccountId yet.
        var walletByEmployeeId = await ReadInDb(db => db.Wallets.SingleAsync(w => w.EmployeeId == employeeId));
        Assert.Equal(100, walletByEmployeeId.Balance);

        // Once AccountProvisioned catches up, the same endpoint reflects the real balance.
        Assert.True(_consumer.HandleWithRetryAndDeadLetter(Provisioned(employeeId, accountId), CancellationToken.None));
        var walletAfterLinking = await ReadOwnWallet(accountId);
        Assert.Equal(100, walletAfterLinking.Balance);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _resetDatabase();

    private async Task<WalletDto> ReadOwnWallet(Guid accountId)
    {
        await using var scope = _services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RewardsDbContext>();
        var controller = new RewardsController(dbContext, new CurrencyGrantWriter(dbContext))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", accountId.ToString())], "test")),
                },
            },
        };

        var response = await controller.GetOwnWallet(CancellationToken.None);
        return Assert.IsType<WalletDto>(Assert.IsType<OkObjectResult>(response.Result).Value);
    }

    private async Task<T> ReadInDb<T>(Func<RewardsDbContext, Task<T>> read)
    {
        await using var scope = _services.CreateAsyncScope();
        return await read(scope.ServiceProvider.GetRequiredService<RewardsDbContext>());
    }

    private static ConsumeResult<string, string> Hired(Guid employeeId) =>
        Build("EmployeeHired", "employee.events", employeeId.ToString(), $$"""{"EmployeeId":"{{employeeId}}"}""");

    private static ConsumeResult<string, string> Provisioned(Guid employeeId, Guid accountId) =>
        Build(
            "AccountProvisioned",
            "auth.events",
            employeeId.ToString(),
            $$"""{"EmployeeId":"{{employeeId}}","AccountId":"{{accountId}}"}""");

    private static ConsumeResult<string, string> Build(string messageType, string topic, string key, string payload)
    {
        var headers = new Headers
        {
            { "message-id", Encoding.UTF8.GetBytes(Guid.NewGuid().ToString()) },
            { "message-type", Encoding.UTF8.GetBytes(messageType) },
        };

        return new ConsumeResult<string, string>
        {
            Topic = topic,
            Message = new Message<string, string> { Key = key, Value = payload, Headers = headers },
        };
    }
}
