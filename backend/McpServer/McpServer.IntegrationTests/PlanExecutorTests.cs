using System.Net;
using McpServer.Agent;
using McpServer.Api;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using static McpServer.IntegrationTests.AgentTestSupport;

namespace McpServer.IntegrationTests;

// What actually leaves McpServer once a user has approved a plan, and what the report says happened.
public class PlanExecutorTests
{
    private static RecordingService OkEmployees(Guid hired) => new((_, _) => RecordingService.Ok(hired));

    private static RecordingService OkRewards() => new((_, _) => RecordingService.Ok(Guid.NewGuid()));

    // ---- as the user, and only what was approved -------------------------------------------------

    [Fact]
    public async Task A_hire_then_grant_runs_both_as_the_caller_and_grants_to_the_person_just_hired()
    {
        var hired = Guid.NewGuid();
        var employees = OkEmployees(hired);
        var rewards = OkRewards();
        var plan = PlanOf(Hire(), GrantToHired(500m));

        var report = await Executor(employees, rewards).ExecuteAsync(plan, CancellationToken.None);

        Assert.True(report.Completed);
        Assert.Equal([StepOutcome.Applied, StepOutcome.Applied], report.Steps.Select(s => s.Outcome).ToArray());
        Assert.Equal(hired, report.Steps[0].CreatedId);

        var hire = Assert.Single(employees.Requests);
        Assert.Equal(CallerToken, hire.Authorization);
        Assert.Equal("/api/employees", hire.Path);
        Assert.Contains("\"fullName\":\"Anna Ivanova\"", hire.Body);
        Assert.Contains("\"email\":\"anna@x.test\"", hire.Body);

        var grant = Assert.Single(rewards.Requests);
        Assert.Equal(CallerToken, grant.Authorization);
        Assert.Contains($"\"employeeId\":\"{hired}\"", grant.Body);
        Assert.Contains("\"amount\":500", grant.Body);
    }

    [Fact]
    public async Task Confirming_the_same_plan_again_sends_the_same_idempotency_key_so_the_ledger_can_recognise_it()
    {
        var rewards = OkRewards();
        var plan = PlanOf(Hire(), GrantToHired());
        var executor = Executor(OkEmployees(Guid.NewGuid()), rewards);

        await executor.ExecuteAsync(plan, CancellationToken.None);
        await executor.ExecuteAsync(plan, CancellationToken.None);

        Assert.Equal(2, rewards.Requests.Count);
        Assert.Equal($"agent-{plan.Id:N}-1", rewards.Requests[0].IdempotencyKey);
        Assert.Equal(rewards.Requests[0].IdempotencyKey, rewards.Requests[1].IdempotencyKey);
    }

    [Fact]
    public async Task Different_plans_never_share_an_idempotency_key()
    {
        var rewards = OkRewards();
        var executor = Executor(OkEmployees(Guid.NewGuid()), rewards);

        await executor.ExecuteAsync(PlanOf(Hire(), GrantToHired()), CancellationToken.None);
        await executor.ExecuteAsync(PlanOf(Hire(), GrantToHired()), CancellationToken.None);

        Assert.NotEqual(rewards.Requests[0].IdempotencyKey, rewards.Requests[1].IdempotencyKey);
    }

    // ---- an honest report when something fails ---------------------------------------------------

    [Fact]
    public async Task When_the_first_step_is_refused_nothing_after_it_is_sent_and_the_report_says_so()
    {
        var employees = new RecordingService((_, _) => RecordingService.Json(HttpStatusCode.Forbidden, """{"secret":"SECRET-DETAIL"}"""));
        var rewards = OkRewards();

        var report = await Executor(employees, rewards).ExecuteAsync(PlanOf(Hire(), GrantToHired()), CancellationToken.None);

        Assert.False(report.Completed);
        Assert.Equal([StepOutcome.Failed, StepOutcome.NotRun], report.Steps.Select(s => s.Outcome).ToArray());
        Assert.Equal("You are not allowed to do this.", report.Steps[0].Detail);
        Assert.DoesNotContain("SECRET-DETAIL", report.Steps[0].Detail);
        Assert.Empty(rewards.Requests);
    }

    [Fact]
    public async Task When_a_later_step_fails_the_earlier_one_is_reported_as_applied_not_hidden()
    {
        var hired = Guid.NewGuid();
        var rewards = new RecordingService((_, _) => RecordingService.Json(HttpStatusCode.Forbidden, "{}"));

        var report = await Executor(OkEmployees(hired), rewards).ExecuteAsync(PlanOf(Hire(), GrantToHired()), CancellationToken.None);

        Assert.False(report.Completed);
        Assert.Equal([StepOutcome.Applied, StepOutcome.Failed], report.Steps.Select(s => s.Outcome).ToArray());
        Assert.Equal(hired, report.Steps[0].CreatedId);
    }

    [Fact]
    public async Task A_duplicate_hire_is_reported_as_a_conflict_and_stops_the_plan()
    {
        var employees = new RecordingService((_, _) => RecordingService.Json(HttpStatusCode.Conflict, "{}"));
        var rewards = OkRewards();

        var report = await Executor(employees, rewards).ExecuteAsync(PlanOf(Hire(), GrantToHired()), CancellationToken.None);

        Assert.Equal("This conflicts with data that already exists.", report.Steps[0].Detail);
        Assert.Empty(rewards.Requests);
    }

    [Fact]
    public async Task With_no_caller_token_nothing_is_sent_and_the_step_fails()
    {
        var employees = OkEmployees(Guid.NewGuid());

        var report = await Executor(employees, OkRewards(), incomingAuthorization: null)
            .ExecuteAsync(PlanOf(Hire()), CancellationToken.None);

        Assert.Empty(employees.Requests);
        Assert.Equal(StepOutcome.Failed, report.Steps[0].Outcome);
    }

    // ---- a signed plan is still checked before anything runs -------------------------------------

    [Fact]
    public async Task A_grant_above_the_ceiling_is_refused_even_though_the_plan_is_signed()
    {
        var rewards = OkRewards();

        var report = await Executor(OkEmployees(Guid.NewGuid()), rewards, maxGrant: 1000m)
            .ExecuteAsync(PlanOf(Hire(), GrantToHired(1000.01m)), CancellationToken.None);

        Assert.Equal(StepOutcome.Applied, report.Steps[0].Outcome);
        Assert.Equal(StepOutcome.Failed, report.Steps[1].Outcome);
        Assert.Empty(rewards.Requests);
    }

    [Theory]
    [InlineData(1)] // itself
    [InlineData(2)] // a step that has not run yet
    [InlineData(-1)]
    public async Task A_grant_can_only_take_its_employee_from_an_earlier_step(int from)
    {
        var rewards = OkRewards();

        var report = await Executor(OkEmployees(Guid.NewGuid()), rewards)
            .ExecuteAsync(PlanOf(Hire(), GrantToHired(from: from)), CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, report.Steps[1].Outcome);
        Assert.Empty(rewards.Requests);
    }

    [Fact]
    public async Task A_grant_cannot_take_its_employee_from_a_step_that_is_not_a_hire()
    {
        var rewards = OkRewards();
        var transfer = new PlanStep(StepKind.TransferEmployee, "Move", EmployeeId: Guid.NewGuid(), DepartmentId: Dept, PositionId: Pos);

        var report = await Executor(OkEmployees(Guid.NewGuid()), rewards)
            .ExecuteAsync(PlanOf(transfer, GrantToHired(from: 0)), CancellationToken.None);

        Assert.Equal(StepOutcome.Applied, report.Steps[0].Outcome);
        Assert.Equal(StepOutcome.Failed, report.Steps[1].Outcome);
        Assert.Empty(rewards.Requests);
    }

    [Fact]
    public async Task A_grant_cannot_mistake_an_earlier_grants_transaction_id_for_an_employee()
    {
        // A grant step also "creates" an id - the transaction's - which must never be read as a person.
        var rewards = OkRewards();
        var plan = PlanOf(Hire(), GrantToHired(from: 0), GrantToHired(from: 1));

        var report = await Executor(OkEmployees(Guid.NewGuid()), rewards).ExecuteAsync(plan, CancellationToken.None);

        Assert.Equal([StepOutcome.Applied, StepOutcome.Applied, StepOutcome.Failed], report.Steps.Select(s => s.Outcome).ToArray());
        Assert.Single(rewards.Requests);
    }

    [Fact]
    public async Task A_grant_that_names_an_employee_two_ways_is_refused()
    {
        var rewards = OkRewards();
        var ambiguous = GrantToHired() with { EmployeeId = Guid.NewGuid() };

        var report = await Executor(OkEmployees(Guid.NewGuid()), rewards)
            .ExecuteAsync(PlanOf(Hire(), ambiguous), CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, report.Steps[1].Outcome);
        Assert.Empty(rewards.Requests);
    }

    [Theory]
    [InlineData("Anna\nIvanova", "anna@x.test")]
    [InlineData("", "anna@x.test")]
    [InlineData("Anna", "not-an-email")]
    [InlineData("Anna", "Anna <anna@x.test>")]
    public async Task A_hire_with_a_broken_name_or_email_is_refused_before_anything_is_sent(string name, string email)
    {
        var employees = OkEmployees(Guid.NewGuid());
        var step = Hire() with { FullName = name, Email = email };

        var report = await Executor(employees, OkRewards()).ExecuteAsync(PlanOf(step), CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, report.Steps[0].Outcome);
        Assert.Empty(employees.Requests);
    }

    [Fact]
    public async Task A_transfer_puts_the_new_placement_on_the_employees_own_resource()
    {
        var employees = OkEmployees(Guid.NewGuid());
        var who = Guid.NewGuid();
        var step = new PlanStep(StepKind.TransferEmployee, "Move", EmployeeId: who, DepartmentId: Dept, PositionId: Pos);

        var report = await Executor(employees, OkRewards()).ExecuteAsync(PlanOf(step), CancellationToken.None);

        Assert.True(report.Completed);
        var sent = Assert.Single(employees.Requests);
        Assert.Equal(HttpMethod.Put, sent.Method);
        Assert.Equal($"/api/employees/{who}/transfer", sent.Path);
        Assert.Equal(CallerToken, sent.Authorization);
    }

    // ---- writes are never retried ----------------------------------------------------------------

    [Fact]
    public async Task A_failing_write_is_attempted_once_not_retried()
    {
        var calls = 0;
        var flaky = new RecordingService((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return RecordingService.Json(HttpStatusCode.ServiceUnavailable, "{}");
        });
        var builder = new ResiliencePipelineBuilder<HttpResponseMessage>();
        ServiceApiResilience.ConfigureForWrites(builder);
        var http = new HttpClient(new ResilienceHandler(builder.Build()) { InnerHandler = flaky })
        {
            BaseAddress = new Uri("http://service.test/"),
        };

        await Assert.ThrowsAsync<ServiceApiException>(() =>
            new EmployeeCommandClient(http).HireAsync("Anna", "a@x.test", Dept, Pos, CancellationToken.None));

        Assert.Equal(1, calls);
    }
}
