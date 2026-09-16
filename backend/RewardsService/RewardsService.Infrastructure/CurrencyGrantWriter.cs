using System.Text.Json;
using RewardsService.Domain;
using RewardsService.Infrastructure.IntegrationEvents;
using Shared.Outbox;

namespace RewardsService.Infrastructure;

// Shared by both grant paths (the manual GrantCurrency endpoint and WelcomeBonusConsumer)
// so the wallet-update + ledger-write + outbox-enqueue sequence exists in exactly one
// place. Does not call SaveChanges itself - the caller owns the transaction boundary,
// same as HireEmployeeHandler enqueues its outbox message and saves once at the end.
public class CurrencyGrantWriter(RewardsDbContext dbContext)
{
    public Transaction Grant(Guid employeeId, decimal amount, string reason, TransactionSource source, Guid? grantedByAccountId)
    {
        var wallet = dbContext.Wallets.SingleOrDefault(w => w.EmployeeId == employeeId);
        if (wallet is null)
        {
            wallet = Wallet.Create(employeeId);
            dbContext.Wallets.Add(wallet);
        }

        wallet.Apply(amount);

        var transaction = Transaction.Create(employeeId, amount, reason, source, grantedByAccountId);
        dbContext.Transactions.Add(transaction);

        var @event = new CurrencyGrantedEvent(employeeId, amount, reason, wallet.Balance);
        dbContext.OutboxMessages.Add(OutboxMessage.Create(
            RewardsEventTypes.CurrencyGranted,
            employeeId.ToString(),
            JsonSerializer.Serialize(@event)));

        return transaction;
    }
}
