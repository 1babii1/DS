using EmployeeService.Web.Temporal;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Testing;
using Temporalio.Worker;

namespace EmployeeService.IntegrationTests;

// The onboarding as a Temporal workflow (ADR 0056), against Temporal's own test server with its clock skipped forward, so a two-minute deadline
// and an hour of grace take milliseconds. The same cases the saga is tested on (HireSagaTests, HireSagaCoordinatorTests): in time, past the
// deadline, a step that arrives late, a failed account, repeated facts; plus the thing that is the reason to have Temporal at all, a worker that
// dies while the timer runs and a deadline that comes due while no worker exists.
public class HireWorkflowTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Grace = TimeSpan.FromHours(1);

    [Fact]
    public async Task Both_steps_in_time_complete_the_hire_and_undo_nothing()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var activities = new RecordingActivities();

        var outcome = await WithWorker(env, activities, async () =>
        {
            var handle = await Start(env);
            await handle.SignalAsync(wf => wf.AccountProvisionedAsync());
            await handle.SignalAsync(wf => wf.BonusGrantedAsync());
            return await handle.GetResultAsync();
        });

        Assert.Equal("Completed", outcome);
        Assert.Empty(activities.Calls);
    }

    [Fact]
    public async Task Repeated_facts_change_nothing_and_ask_for_nothing()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var activities = new RecordingActivities();

        var outcome = await WithWorker(env, activities, async () =>
        {
            var handle = await Start(env);
            await handle.SignalAsync(wf => wf.AccountProvisionedAsync());
            await handle.SignalAsync(wf => wf.AccountProvisionedAsync());
            await handle.SignalAsync(wf => wf.BonusGrantedAsync());
            await handle.SignalAsync(wf => wf.BonusGrantedAsync());
            return await handle.GetResultAsync();
        });

        Assert.Equal("Completed", outcome);
        Assert.Empty(activities.Calls);
    }

    [Fact]
    public async Task A_missed_deadline_undoes_the_steps_that_happened_and_only_those()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var activities = new RecordingActivities();

        var outcome = await WithWorker(env, activities, async () =>
        {
            var handle = await Start(env);
            await handle.SignalAsync(wf => wf.AccountProvisionedAsync());
            return await handle.GetResultAsync();
        });

        Assert.Equal("CompensationRequested", outcome);
        var call = Assert.Single(activities.Calls);
        Assert.Equal("Onboarding did not complete in time", call.Reason);
        Assert.True(call.RevokeAccount);
        Assert.False(call.ReverseBonus);
    }

    [Fact]
    public async Task A_step_that_finishes_after_the_decision_is_undone_in_a_second_narrower_request()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var activities = new RecordingActivities();

        await WithWorker(env, activities, async () =>
        {
            var handle = await Start(env);
            await handle.SignalAsync(wf => wf.AccountProvisionedAsync());
            await env.DelayAsync(Timeout + TimeSpan.FromMinutes(1));
            await handle.SignalAsync(wf => wf.BonusGrantedAsync());
            return await handle.GetResultAsync();
        });

        Assert.Equal(2, activities.Calls.Count);
        Assert.True(activities.Calls[0].RevokeAccount);
        Assert.False(activities.Calls[0].ReverseBonus);
        Assert.False(activities.Calls[1].RevokeAccount);
        Assert.True(activities.Calls[1].ReverseBonus);
    }

    [Fact]
    public async Task A_workflow_started_late_counts_its_deadline_from_the_hire_and_acts_at_once()
    {
        // Temporal was down when the hire committed, or the first fact to arrive started the workflow: the hire is older than the workflow.
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var activities = new RecordingActivities();

        await WithWorker(env, activities, async () =>
        {
            var handle = await env.Client.StartWorkflowAsync(
                (HireWorkflow wf) => wf.RunAsync(new HireWorkflowInput(Guid.NewGuid(), Timeout, Grace, DateTime.UtcNow - Timeout - TimeSpan.FromMinutes(1))),
                new WorkflowOptions($"hire-{Guid.NewGuid():N}", "hire-test"));

            // The two minutes were up a minute ago, so nothing is waited for.
            await env.DelayAsync(TimeSpan.FromSeconds(5));
            Assert.Single(activities.Calls);
            return await handle.GetResultAsync();
        });

        Assert.Equal("Onboarding did not complete in time", activities.Calls[0].Reason);
    }

    [Fact]
    public async Task A_failed_account_undoes_the_hire_at_once_without_waiting_for_the_deadline()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var activities = new RecordingActivities();

        await WithWorker(env, activities, async () =>
        {
            var handle = await Start(env);
            await handle.SignalAsync(wf => wf.AccountProvisioningFailedAsync("the email is taken"));

            // Long before the two minutes are up.
            await env.DelayAsync(TimeSpan.FromSeconds(5));
            Assert.Single(activities.Calls);
            return await handle.GetResultAsync();
        });

        Assert.Contains("the email is taken", activities.Calls[0].Reason);
        Assert.False(activities.Calls[0].RevokeAccount);
    }

    [Fact]
    public async Task A_deadline_that_comes_due_while_no_worker_is_alive_is_acted_on_when_a_worker_returns()
    {
        // Real time and a real server, not the one with a skipped clock: that server does not move time while a task waits for a worker, and
        // "nobody is there when the deadline comes" is exactly what this case is. The intervals are short instead.
        await using var env = await WorkflowEnvironment.StartLocalAsync();
        var first = new RecordingActivities();
        var second = new RecordingActivities();
        var taskQueue = $"hire-down-{Guid.NewGuid():N}";
        var input = new HireWorkflowInput(Guid.NewGuid(), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4));

        // The first worker starts the process and hears the account is provisioned. Then it goes away, as when its process is killed.
        var handle = await Worker(env, taskQueue, first, async () =>
        {
            var started = await env.Client.StartWorkflowAsync(
                (HireWorkflow wf) => wf.RunAsync(input),
                new WorkflowOptions($"hire-{Guid.NewGuid():N}", taskQueue));
            await started.SignalAsync(wf => wf.AccountProvisionedAsync());
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            return started;
        });
        Assert.Empty(first.Calls);

        // The three seconds pass with nobody there to see it. Nothing is lost, because the timer is Temporal's and not a worker's.
        await Task.Delay(TimeSpan.FromSeconds(5));
        Assert.Empty(first.Calls);
        Assert.Empty(second.Calls);

        // A new worker, another object that remembers nothing, takes the workflow from its history and does what the deadline asked for.
        var outcome = await Worker(env, taskQueue, second, () => handle.GetResultAsync());

        Assert.Equal("CompensationRequested", outcome);
        var call = Assert.Single(second.Calls);
        Assert.Equal("Onboarding did not complete in time", call.Reason);
        Assert.True(call.RevokeAccount);
        Assert.Empty(first.Calls);
    }

    private static Task<WorkflowHandle<HireWorkflow, string>> Start(WorkflowEnvironment env) =>
        env.Client.StartWorkflowAsync(
            (HireWorkflow wf) => wf.RunAsync(new HireWorkflowInput(Guid.NewGuid(), Timeout, Grace)),
            new WorkflowOptions($"hire-{Guid.NewGuid():N}", "hire-test"));

    private static async Task<T> Worker<T>(WorkflowEnvironment env, string taskQueue, RecordingActivities activities, Func<Task<T>> body)
    {
        using var worker = new TemporalWorker(
            env.Client,
            new TemporalWorkerOptions(taskQueue).AddWorkflow<HireWorkflow>().AddAllActivities(activities));
        return await worker.ExecuteAsync(body);
    }

    private static async Task<T> WithWorker<T>(WorkflowEnvironment env, RecordingActivities activities, Func<Task<T>> body)
    {
        using var worker = new TemporalWorker(
            env.Client,
            new TemporalWorkerOptions("hire-test").AddWorkflow<HireWorkflow>().AddAllActivities(activities));
        return await worker.ExecuteAsync(body);
    }

    // Stands in for HireActivities: the same activity under the same name, recording instead of touching a database.
    private sealed class RecordingActivities
    {
        private readonly List<CompensateInput> calls = [];

        public IReadOnlyList<CompensateInput> Calls
        {
            get
            {
                lock (calls)
                {
                    return calls.ToList();
                }
            }
        }

        [Activity]
        public Task CompensateAsync(CompensateInput input)
        {
            lock (calls)
            {
                calls.Add(input);
            }

            return Task.CompletedTask;
        }
    }
}
