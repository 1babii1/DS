using EmployeeService.Domain;

namespace EmployeeService.IntegrationTests;

// The onboarding process as a pure state machine (ADR 0032): facts and a clock in, decisions out.
public class HireSagaTests
{
    private static readonly DateTime T0 = new(2026, 10, 3, 9, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    private static HireSaga Started() => HireSaga.Start(Guid.NewGuid(), T0, Timeout);

    [Fact]
    public void It_completes_when_the_account_and_the_bonus_both_arrive_in_either_order()
    {
        var a = Started();
        Assert.Null(a.OnAccountProvisioned(T0.AddSeconds(1)));
        Assert.Equal(HireSagaState.Started, a.State);
        Assert.Null(a.OnBonusGranted(T0.AddSeconds(2)));
        Assert.Equal(HireSagaState.Completed, a.State);
        Assert.Equal(T0.AddSeconds(2), a.CompletedAt);

        var b = Started();
        b.OnBonusGranted(T0.AddSeconds(1));
        b.OnAccountProvisioned(T0.AddSeconds(2));
        Assert.Equal(HireSagaState.Completed, b.State);
    }

    [Fact]
    public void Nothing_happens_before_the_deadline_and_a_completed_process_never_expires()
    {
        var saga = Started();
        Assert.Null(saga.OnTimeCheck(T0 + Timeout - TimeSpan.FromMilliseconds(1)));
        Assert.Equal(HireSagaState.Started, saga.State);

        saga.OnAccountProvisioned(T0.AddSeconds(5));
        saga.OnBonusGranted(T0.AddSeconds(6));
        Assert.Null(saga.OnTimeCheck(T0.AddHours(1)));
        Assert.Equal(HireSagaState.Completed, saga.State);
    }

    [Fact]
    public void At_the_deadline_it_undoes_exactly_the_steps_that_happened()
    {
        var onlyAccount = Started();
        onlyAccount.OnAccountProvisioned(T0.AddSeconds(5));
        var plan = onlyAccount.OnTimeCheck(T0 + Timeout);
        Assert.Equal(new Compensation("Onboarding did not complete in time", RevokeAccount: true, ReverseBonus: false), plan);
        Assert.Equal(HireSagaState.CompensationRequested, onlyAccount.State);

        var onlyBonus = Started();
        onlyBonus.OnBonusGranted(T0.AddSeconds(5));
        Assert.Equal(new Compensation("Onboarding did not complete in time", RevokeAccount: false, ReverseBonus: true), onlyBonus.OnTimeCheck(T0 + Timeout));

        var neither = Started();
        Assert.Equal(new Compensation("Onboarding did not complete in time", RevokeAccount: false, ReverseBonus: false), neither.OnTimeCheck(T0 + Timeout));
    }

    [Fact]
    public void A_deadline_is_acted_on_once()
    {
        var saga = Started();
        Assert.NotNull(saga.OnTimeCheck(T0 + Timeout));

        Assert.Null(saga.OnTimeCheck(T0 + Timeout.Add(TimeSpan.FromMinutes(5))));
    }

    [Fact]
    public void A_failed_account_compensates_at_once_without_waiting_for_the_deadline()
    {
        var saga = Started();

        var plan = saga.OnAccountProvisioningFailed("email taken", T0.AddSeconds(3));

        Assert.NotNull(plan);
        Assert.Contains("email taken", plan.Reason);
        Assert.False(plan.RevokeAccount);
        Assert.Equal(HireSagaState.CompensationRequested, saga.State);
        Assert.Null(saga.OnAccountProvisioningFailed("email taken", T0.AddSeconds(4)));
    }

    [Fact]
    public void Redelivered_facts_change_nothing_and_ask_for_nothing()
    {
        var saga = Started();
        saga.OnAccountProvisioned(T0.AddSeconds(1));

        Assert.Null(saga.OnAccountProvisioned(T0.AddSeconds(2)));
        Assert.Equal(HireSagaState.Started, saga.State);

        saga.OnBonusGranted(T0.AddSeconds(3));
        Assert.Null(saga.OnBonusGranted(T0.AddSeconds(4)));
        Assert.Equal(HireSagaState.Completed, saga.State);
        Assert.Equal(T0.AddSeconds(3), saga.CompletedAt);
    }

    [Fact]
    public void A_step_that_finishes_after_the_compensation_was_requested_is_asked_to_be_undone_too_once()
    {
        var saga = Started();
        saga.OnTimeCheck(T0 + Timeout);

        // The account is made a moment after the deadline: the first compensation did not cover it.
        var late = saga.OnAccountProvisioned(T0 + Timeout + TimeSpan.FromSeconds(1));

        Assert.NotNull(late);
        Assert.True(late.RevokeAccount);
        Assert.False(late.ReverseBonus);
        Assert.Null(saga.OnAccountProvisioned(T0 + Timeout + TimeSpan.FromSeconds(2)));

        var lateBonus = saga.OnBonusGranted(T0 + Timeout + TimeSpan.FromSeconds(3));
        Assert.NotNull(lateBonus);
        Assert.True(lateBonus.ReverseBonus);
        Assert.False(lateBonus.RevokeAccount);
        Assert.Equal(HireSagaState.CompensationRequested, saga.State);
    }

    [Fact]
    public void A_step_covered_by_the_compensation_is_not_asked_about_again()
    {
        var saga = Started();
        saga.OnAccountProvisioned(T0.AddSeconds(1));
        saga.OnTimeCheck(T0 + Timeout);

        Assert.Null(saga.OnAccountProvisioned(T0 + Timeout + TimeSpan.FromSeconds(1)));
    }
}
