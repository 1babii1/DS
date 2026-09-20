using System.Security.Claims;
using System.Text;
using Confluent.Kafka;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
