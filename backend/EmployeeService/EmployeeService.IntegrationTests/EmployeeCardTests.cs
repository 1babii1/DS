using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using EmployeeService.Application.Database;
using EmployeeService.Application.Employees.Commands;
using EmployeeService.Application.Employees.Queries;
using EmployeeService.Infrastructure.Postgres;
using EmployeeService.Web.Consumers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EmployeeService.IntegrationTests;

// The employee card (ADR 0034): this service's own employee row plus a copy of the wallet kept from RewardsService's events.
// Events go through the real consumer, reads through the real handler, both against the real database.
public class EmployeeCardTests : IClassFixture<EmployeeTestWebFactory>, IAsyncLifetime
{
    private readonly Func<Task> _resetDatabase;
    private readonly IServiceProvider _services;
    private readonly AuthEventsConsumer _sut;

    public EmployeeCardTests(EmployeeTestWebFactory factory)
    {
        _services = factory.Services;
        _resetDatabase = factory.ResetDatabaseAsync;
        _sut = new AuthEventsConsumer(
            _services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new EmployeeConsumerOptions()),
            _services.GetRequiredService<ILogger<AuthEventsConsumer>>());
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _resetDatabase();

    [Fact]
    public async Task A_card_without_any_wallet_event_yet_has_no_balance_and_says_so()
    {
        var employee = await HireAsync();

        var card = await ReadCard(employee, atLeast: null);

        Assert.NotNull(card);
        Assert.Null(card.Balance);
        Assert.Null(card.BalanceAsOfVersion);
        Assert.True(card.Consistent);
        Assert.Equal(employee, card.Employee.Id);
    }

    [Fact]
    public async Task A_wallet_event_puts_the_balance_and_its_version_on_the_card()
    {
        var employee = await HireAsync();

        Deliver(employee, balance: 100, version: 1);

        var card = await ReadCard(employee, atLeast: null);
        Assert.Equal(100, card!.Balance);
        Assert.Equal(1, card.BalanceAsOfVersion);
    }

    [Fact]
    public async Task An_older_event_arriving_after_a_newer_one_does_not_move_the_balance_back()
    {
        var employee = await HireAsync();
        Deliver(employee, balance: 150, version: 3);

        Deliver(employee, balance: 100, version: 1);

        var card = await ReadCard(employee, atLeast: null);
        Assert.Equal(150, card!.Balance);
        Assert.Equal(3, card.BalanceAsOfVersion);
    }

    [Fact]
    public async Task The_same_event_delivered_twice_changes_nothing_the_second_time()
    {
        var employee = await HireAsync();
        var message = Message(employee, balance: 100, version: 1);

        Assert.True(_sut.HandleWithRetryAndDeadLetter(message, CancellationToken.None));
        var first = await Read(db => db.EmployeeWallets.AsNoTracking().SingleAsync(w => w.EmployeeId == employee));
        await Task.Delay(30);
        Assert.True(_sut.HandleWithRetryAndDeadLetter(message, CancellationToken.None));
        var second = await Read(db => db.EmployeeWallets.AsNoTracking().SingleAsync(w => w.EmployeeId == employee));

        // Untouched: not even the time the copy was taken moved.
        Assert.Equal(first.ProjectedAt, second.ProjectedAt);
        Assert.Equal(100, second.Balance);
    }

    [Fact]
    public async Task A_reversal_is_a_lower_balance_like_any_other_change()
    {
        var employee = await HireAsync();
        Deliver(employee, balance: 100, version: 1, source: "WelcomeBonus");

        Deliver(employee, balance: 0, version: 2, source: "WelcomeBonusReversal");

        Assert.Equal(0, (await ReadCard(employee, null))!.Balance);
    }

    [Fact]
    public async Task An_event_without_a_wallet_version_cannot_be_ordered_and_is_left_out()
    {
        var employee = await HireAsync();

        Deliver(employee, balance: 100, version: null);

        Assert.Null((await ReadCard(employee, null))!.Balance);
    }

    [Fact]
    public async Task Events_applied_at_once_in_any_order_end_at_the_highest_version()
    {
        var employee = await HireAsync();
        var versions = Enumerable.Range(1, 12).OrderBy(_ => Random.Shared.Next()).ToArray();

        using var barrier = new Barrier(versions.Length);
        var threads = versions.Select(v => new Thread(() =>
        {
            barrier.SignalAndWait();
            _sut.HandleWithRetryAndDeadLetter(Message(employee, balance: v * 10, version: v), CancellationToken.None);
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        var card = await ReadCard(employee, null);
        Assert.Equal(12, card!.BalanceAsOfVersion);
        Assert.Equal(120, card.Balance);
    }

    [Fact]
    public async Task A_read_that_asks_for_a_version_waits_until_the_copy_reaches_it()
    {
        var employee = await HireAsync();
        Deliver(employee, balance: 100, version: 1);

        var late = Task.Run(async () =>
        {
            await Task.Delay(150);
            Deliver(employee, balance: 130, version: 2);
        });
        var card = await ReadCard(employee, atLeast: 2, maxWait: TimeSpan.FromSeconds(3));
        await late;

        Assert.True(card!.Consistent);
        Assert.Equal(130, card.Balance);
        Assert.Equal(2, card.BalanceAsOfVersion);
    }

    [Fact]
    public async Task A_read_that_waits_in_vain_returns_the_card_it_has_marked_as_behind()
    {
        var employee = await HireAsync();
        Deliver(employee, balance: 100, version: 1);

        var started = DateTime.UtcNow;
        var card = await ReadCard(employee, atLeast: 5, maxWait: TimeSpan.FromMilliseconds(300));

        Assert.False(card!.Consistent);
        Assert.Equal(100, card.Balance);
        Assert.InRange(DateTime.UtcNow - started, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task A_card_for_someone_who_does_not_exist_is_nothing()
    {
        Assert.Null(await ReadCard(Guid.NewGuid(), atLeast: null));
    }

    private async Task<Guid> HireAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<HireEmployeeHandler>().Handle(
            new HireEmployeeCommand($"Card {Guid.NewGuid():N}", $"card-{Guid.NewGuid():N}@test.local", Guid.NewGuid(), Guid.NewGuid()),
            CancellationToken.None);
        Assert.True(result.IsSuccess);
        return result.Value;
    }

    private async Task<EmployeeCardDto?> ReadCard(Guid employee, int? atLeast, TimeSpan? maxWait = null)
    {
        await using var scope = _services.CreateAsyncScope();
        var handler = new GetEmployeeCardHandler(
            scope.ServiceProvider.GetRequiredService<IReadDbContext>(),
            Options.Create(new EmployeeCardOptions { MaxWait = maxWait ?? TimeSpan.FromMilliseconds(300), PollInterval = TimeSpan.FromMilliseconds(10) }),
            TimeProvider.System);
        return await handler.Handle(employee, atLeast, CancellationToken.None);
    }

    private async Task<T> Read<T>(Func<EmployeeDbContext, Task<T>> read)
    {
        await using var scope = _services.CreateAsyncScope();
        return await read(scope.ServiceProvider.GetRequiredService<EmployeeDbContext>());
    }

    private void Deliver(Guid employee, decimal balance, int? version, string? source = null) =>
        Assert.True(_sut.HandleWithRetryAndDeadLetter(Message(employee, balance, version, source), CancellationToken.None));

    private static ConsumeResult<string, string> Message(Guid employee, decimal balance, int? version, string? source = null)
    {
        var headers = new Headers
        {
            { "message-id", Encoding.UTF8.GetBytes(Guid.NewGuid().ToString()) },
            { "message-type", Encoding.UTF8.GetBytes("CurrencyGranted") },
        };
        var payload = JsonSerializer.Serialize(new { EmployeeId = employee, Amount = 0, Reason = "x", NewBalance = balance, Source = source, WalletVersion = version });
        return new ConsumeResult<string, string>
        {
            Topic = "rewards.events",
            Message = new Message<string, string> { Key = employee.ToString(), Value = payload, Headers = headers },
        };
    }
}
