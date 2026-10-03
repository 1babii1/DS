namespace EmployeeService.Domain;

public enum HireSagaState
{
    /// <summary>Waiting for the account and the welcome bonus.</summary>
    Started = 0,

    /// <summary>Both arrived in time.</summary>
    Completed = 1,

    /// <summary>The deadline passed, or the account could not be made: the steps that happened have been asked to be undone.</summary>
    CompensationRequested = 2,
}

/// <summary>What the process wants done next, decided by the saga and carried out by whoever called it.</summary>
public sealed record Compensation(string Reason, bool RevokeAccount, bool ReverseBonus);

/// <summary>
/// The onboarding of a hired person as one process with a memory (ADR 0032): their login account must be provisioned and their
/// welcome bonus granted, both within a deadline, and if that does not happen the steps that did happen are undone. One
/// participant, EmployeeService, holds the whole state, so "where is this hire?" has an answer in one row, and the deadline and
/// the compensation live in one place instead of being implied by who listens to what.
///
/// Pure: it takes facts and a clock and returns decisions. It stores nothing and sends nothing.
/// </summary>
public class HireSaga
{
    public Guid EmployeeId { get; private set; }

    public HireSagaState State { get; private set; }

    public DateTime StartedAt { get; private set; }

    public DateTime Deadline { get; private set; }

    public bool AccountProvisioned { get; private set; }

    public bool BonusGranted { get; private set; }

    public DateTime? CompletedAt { get; private set; }

    public DateTime? CompensationRequestedAt { get; private set; }

    public string? CompensationReason { get; private set; }

    // What the compensation already asked for. A step that arrives after the compensation was requested (the account is made
    // moments after the deadline, say) was not covered by it and has to be asked about again.
    public bool AccountRevocationRequested { get; private set; }

    public bool BonusReversalRequested { get; private set; }

    private HireSaga()
    {
    }

    public static HireSaga Start(Guid employeeId, DateTime now, TimeSpan timeout) => new()
    {
        EmployeeId = employeeId,
        State = HireSagaState.Started,
        StartedAt = now,
        Deadline = now + timeout,
    };

    /// <summary>The login account exists. Idempotent: the same fact twice changes nothing and asks for nothing.</summary>
    public Compensation? OnAccountProvisioned(DateTime now)
    {
        if (AccountProvisioned)
        {
            return null;
        }

        AccountProvisioned = true;
        return AfterStep(now);
    }

    /// <summary>The welcome bonus was granted. Idempotent.</summary>
    public Compensation? OnBonusGranted(DateTime now)
    {
        if (BonusGranted)
        {
            return null;
        }

        BonusGranted = true;
        return AfterStep(now);
    }

    /// <summary>The account could not be made (for example the email is taken). Nothing waits for it any more.</summary>
    public Compensation? OnAccountProvisioningFailed(string reason, DateTime now) =>
        State == HireSagaState.Started ? Compensate($"The account could not be provisioned: {reason}", now) : null;

    /// <summary>Called when time passes. Does nothing before the deadline or once the process is over.</summary>
    public Compensation? OnTimeCheck(DateTime now) =>
        State == HireSagaState.Started && now >= Deadline
            ? Compensate("Onboarding did not complete in time", now)
            : null;

    private Compensation? AfterStep(DateTime now)
    {
        switch (State)
        {
            case HireSagaState.Started when AccountProvisioned && BonusGranted:
                State = HireSagaState.Completed;
                CompletedAt = now;
                return null;

            case HireSagaState.CompensationRequested:
                // A step finished after we decided to undo the process: it was not part of what we asked to undo.
                return LateStepCompensation();

            default:
                return null;
        }
    }

    private Compensation Compensate(string reason, DateTime now)
    {
        State = HireSagaState.CompensationRequested;
        CompensationRequestedAt = now;
        CompensationReason = reason;
        AccountRevocationRequested = AccountProvisioned;
        BonusReversalRequested = BonusGranted;
        return new Compensation(reason, RevokeAccount: AccountProvisioned, ReverseBonus: BonusGranted);
    }

    private Compensation? LateStepCompensation()
    {
        var revoke = AccountProvisioned && !AccountRevocationRequested;
        var reverse = BonusGranted && !BonusReversalRequested;
        if (!revoke && !reverse)
        {
            return null;
        }

        AccountRevocationRequested |= revoke;
        BonusReversalRequested |= reverse;
        return new Compensation(CompensationReason ?? "Onboarding did not complete", revoke, reverse);
    }
}
