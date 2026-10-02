using System.Text.Json;

namespace RewardsService.Domain;

public static class WalletEventTypes
{
    public const string Adjusted = "WalletAdjusted";
}

/// <summary>What a wallet adjustment records; stored as the event's data and enough to rebuild the ledger row from it.</summary>
public sealed record WalletAdjustedData(
    Guid TransactionId,
    decimal Amount,
    string Reason,
    string Source,
    Guid? GrantedByAccountId);

/// <summary>
/// One fact about one wallet, kept for good (event sourcing, ADR 0031). The events of a wallet are the source of truth: the
/// balance, the ledger rows and everything else that describes a wallet is derived from them and can be rebuilt. The pair
/// (StreamId, Version) is the primary key, so two writers that both think they are appending version N cannot both succeed;
/// that is the whole concurrency control.
/// </summary>
public class WalletEvent
{
    public Guid StreamId { get; private set; }

    public int Version { get; private set; }

    public string EventType { get; private set; } = null!;

    public string Data { get; private set; } = null!;

    public DateTime OccurredAt { get; private set; }

    private WalletEvent()
    {
    }

    public static WalletEvent Adjusted(Guid streamId, int version, WalletAdjustedData data) => new()
    {
        StreamId = streamId,
        Version = version,
        EventType = WalletEventTypes.Adjusted,
        Data = JsonSerializer.Serialize(data),
        OccurredAt = DateTime.UtcNow,
    };

    public WalletAdjustedData ReadAdjusted() =>
        JsonSerializer.Deserialize<WalletAdjustedData>(Data)
        ?? throw new InvalidOperationException($"Wallet event {StreamId}/{Version} has unreadable data");
}

public sealed class WelcomeBonusAlreadyGrantedException(Guid employeeId)
    : InvalidOperationException($"Employee {employeeId} has already been given the welcome bonus")
{
}

/// <summary>
/// A wallet as the fold of its events: the balance, the version to append next, and whether the welcome bonus is already in.
/// Deciding a change is a method here, so the rules live with the state they depend on and not in whoever calls it.
/// </summary>
public sealed class WalletAggregate
{
    private WalletAggregate(Guid employeeId)
    {
        EmployeeId = employeeId;
    }

    public Guid EmployeeId { get; }

    public decimal Balance { get; private set; }

    /// <summary>The version of the last event applied; the next one appended is this plus one.</summary>
    public int Version { get; private set; }

    public bool HasWelcomeBonus { get; private set; }

    public static WalletAggregate Rehydrate(Guid employeeId, IEnumerable<WalletEvent> events)
    {
        var wallet = new WalletAggregate(employeeId);
        foreach (var @event in events.OrderBy(e => e.Version))
        {
            if (@event.Version != wallet.Version + 1)
            {
                throw new InvalidOperationException(
                    $"Wallet {employeeId} has a gap in its history: after version {wallet.Version} comes {@event.Version}");
            }

            wallet.Apply(@event);
        }

        return wallet;
    }

    /// <summary>Decides an adjustment and applies it. Returns the event to be stored; nothing is stored here.</summary>
    public WalletEvent Adjust(Guid transactionId, decimal amount, string reason, TransactionSource source, Guid? grantedByAccountId)
    {
        if (source == TransactionSource.WelcomeBonus && HasWelcomeBonus)
        {
            throw new WelcomeBonusAlreadyGrantedException(EmployeeId);
        }

        var @event = WalletEvent.Adjusted(
            EmployeeId, Version + 1, new WalletAdjustedData(transactionId, amount, reason, source.ToString(), grantedByAccountId));
        Apply(@event);
        return @event;
    }

    private void Apply(WalletEvent @event)
    {
        var data = @event.ReadAdjusted();
        Balance += data.Amount;
        Version = @event.Version;
        if (data.Source == nameof(TransactionSource.WelcomeBonus))
        {
            HasWelcomeBonus = true;
        }
    }
}
