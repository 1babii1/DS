using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RewardsService.Domain;
using RewardsService.Infrastructure.IntegrationEvents;
using Shared.Avro;
using Shared.Outbox;

namespace RewardsService.Infrastructure;

// Shared by both grant paths (the manual GrantCurrency endpoint and WelcomeBonusConsumer) so that deciding a grant, recording
// it and telling the bus exist in exactly one place. A grant is an event appended to the wallet's own history (ADR 0031); the
// wallet row and the ledger row are projections written beside it, and the outbox message tells the other services.
// Does not call SaveChanges itself - the caller owns the transaction boundary: the event, both projections and the outbox
// message commit together or not at all, and a collision with another writer on the same wallet surfaces there, as a
// unique violation of the events' key, for the caller to retry.
public class CurrencyGrantWriter(RewardsDbContext dbContext, IEventAvroEncoder? avro = null)
{
    public Transaction Grant(Guid employeeId, decimal amount, string reason, TransactionSource source, Guid? grantedByAccountId)
    {
        // The wallet as its history says it is, not as the cached row says: the cached row is what this is about to update.
        var history = dbContext.WalletEvents.AsNoTracking().Where(e => e.StreamId == employeeId).OrderBy(e => e.Version).ToList();
        var aggregate = WalletAggregate.Rehydrate(employeeId, history);

        var transaction = Transaction.Create(employeeId, amount, reason, source, grantedByAccountId);
        dbContext.WalletEvents.Add(aggregate.Adjust(transaction.Id, amount, reason, source, grantedByAccountId));

        var wallet = dbContext.Wallets.SingleOrDefault(w => w.EmployeeId == employeeId);
        if (wallet is null)
        {
            wallet = Wallet.Create(employeeId);
            dbContext.Wallets.Add(wallet);
        }

        wallet.SetBalance(aggregate.Balance);
        dbContext.Transactions.Add(transaction);

        var @event = new CurrencyGrantedEvent(employeeId, amount, reason, aggregate.Balance);
        var message = OutboxMessage.Create(
            RewardsEventTypes.CurrencyGranted,
            employeeId.ToString(),
            JsonSerializer.Serialize(@event));
        message.StageAvro(avro, RewardsEventTypes.CurrencyGranted, @event);

        dbContext.OutboxMessages.Add(message);

        return transaction;
    }
}
