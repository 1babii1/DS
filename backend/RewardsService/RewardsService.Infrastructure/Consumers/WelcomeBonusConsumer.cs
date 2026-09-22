using System.Text.Json;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RewardsService.Domain;
using Shared.Database;
using Shared.Kafka;

namespace RewardsService.Infrastructure.Consumers;

// Listens on employee.events and auth.events; everything else on those topics is
// deliberately ignored. The consume loop, retry budget and dead-lettering come from
// KafkaRetryConsumer.
public class WelcomeBonusConsumer(
    IServiceScopeFactory scopeFactory,
    IOptions<WelcomeBonusConsumerOptions> options,
    ILogger<WelcomeBonusConsumer> logger)
    : KafkaRetryConsumer<RewardsDbContext>(scopeFactory, options.Value, logger)
{
    private readonly WelcomeBonusConsumerOptions _options = options.Value;

    protected override string MessageKind => "rewards";

    protected override Task ProcessMessageAsync(
        ConsumeResult<string, string> result, CancellationToken cancellationToken)
    {
        var messageId = GetHeader(result.Message.Headers, "message-id");
        var messageType = GetHeader(result.Message.Headers, "message-type");

        if (messageId is null || !Guid.TryParse(messageId, out _))
        {
            Logger.LogWarning("Skipping message without a valid message-id header on topic {Topic}", result.Topic);
            return Task.CompletedTask;
        }

        if (messageType == AccountProvisionedEvent.MessageType)
        {
            RecordAccountLookup(result.Message.Value);
            return Task.CompletedTask;
        }

        if (messageType != EmployeeHiredEvent.MessageType)
        {
            // Nothing else on these topics affects a wallet.
            return Task.CompletedTask;
        }

        GrantWelcomeBonus(result.Message.Value);
        return Task.CompletedTask;
    }

    private void GrantWelcomeBonus(string payload)
    {
        var hired = JsonSerializer.Deserialize<EmployeeHiredEvent>(payload)
            ?? throw new InvalidOperationException($"Could not deserialize {EmployeeHiredEvent.MessageType} payload");

        using var scope = ScopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RewardsDbContext>();
        var writer = new CurrencyGrantWriter(dbContext);

        // Idempotency: an EmployeeHired event redelivered after this already ran must not grant a
        // second welcome bonus. On its own this check is check-then-act (confirmed by reproducing
        // a 16x duplicate grant with only this check and no database constraint) - what actually
        // makes it race-safe is RewardsDbContext's partial unique index on
        // (EmployeeId WHERE Source = 'WelcomeBonus'), which turns the losing racer's SaveChanges
        // into a unique-violation exception instead of a second, silently accepted row.
        if (dbContext.Transactions.Any(t => t.EmployeeId == hired.EmployeeId && t.Source == TransactionSource.WelcomeBonus))
        {
            return;
        }

        writer.Grant(hired.EmployeeId, _options.WelcomeBonusAmount, "Welcome bonus", TransactionSource.WelcomeBonus, null);

        try
        {
            dbContext.SaveChanges();
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            // Lost the race - another concurrent/redelivered attempt already committed its own
            // welcome-bonus transaction. The unique index is what makes this race-safe regardless
            // (an uncaught exception here would still resolve correctly: KafkaRetryConsumer retries,
            // and the Any() check above would see the committed row on the next attempt) - this
            // catch only avoids the 200ms retry delay and a misleading "failed to process" warning
            // for what is actually the expected, benign outcome of two deliveries racing. Narrowed
            // to the specific constraint so an unrelated failure (a genuine connectivity problem,
            // say) surfaces and retries instead of being swallowed as if it were this.
        }
    }

    // Wallets are keyed by EmployeeId, but a caller's JWT only carries their AccountId.
    // Recording the pair as it arrives is what lets GET /api/rewards/wallet find the
    // caller's own wallet at all.
    private void RecordAccountLookup(string payload)
    {
        var @event = JsonSerializer.Deserialize<AccountProvisionedEvent>(payload)
            ?? throw new InvalidOperationException($"Could not deserialize {AccountProvisionedEvent.MessageType} payload");

        using var scope = ScopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RewardsDbContext>();

        if (dbContext.AccountLookups.Any(l => l.EmployeeId == @event.EmployeeId))
        {
            return;
        }

        dbContext.AccountLookups.Add(AccountLookup.Create(@event.EmployeeId, @event.AccountId));

        try
        {
            dbContext.SaveChanges();
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            // Lost a race with another consumer instance - AccountLookup's own primary key
            // (EmployeeId) already made this race-safe; narrowed for the same reason as above.
        }
    }
}
