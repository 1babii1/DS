using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using McpServer.Agent;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using static McpServer.IntegrationTests.AgentTestSupport;

namespace McpServer.IntegrationTests;

// What is countable about the agent, checked against the instruments of the instance under test only, so
// tests running in parallel elsewhere cannot leak measurements into these.
public class AgentTelemetryTests
{
    private sealed class Clock : TimeProvider;

    internal sealed class Capture : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ActivityListener _spans;

        public List<(string Instrument, Dictionary<string, object?> Tags, long Value)> Measurements { get; } = [];

        public List<Activity> Activities { get; } = [];

        public Capture(AgentTelemetry telemetry)
        {
            _listener.InstrumentPublished = (instrument, l) =>
            {
                if (ReferenceEquals(instrument.Meter, telemetry.Meter))
                {
                    l.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
                Measurements.Add((instrument.Name, tags.ToArray().ToDictionary(t => t.Key, t => t.Value), value)));
            _listener.Start();

            _spans = new ActivityListener
            {
                ShouldListenTo = source => ReferenceEquals(source, telemetry.Source),
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = Activities.Add,
            };
            ActivitySource.AddActivityListener(_spans);
        }

        public long Sum(string instrument, params (string Key, string Value)[] where) => Measurements
            .Where(m => m.Instrument == instrument && where.All(w => Equals(m.Tags.GetValueOrDefault(w.Key), w.Value)))
            .Sum(m => m.Value);

        public void Dispose()
        {
            _listener.Dispose();
            _spans.Dispose();
        }
    }

    private static AgentTools Tools(AgentTelemetry telemetry, bool signedIn = true)
    {
        var context = new DefaultHttpContext();
        if (signedIn)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", Guid.NewGuid().ToString())], "test"));
        }

        return new AgentTools(
            new HttpContextAccessor { HttpContext = context },
            new PlanSigner(RandomNumberGenerator.GetBytes(32), new Clock()),
            Options.Create(new AgentOptions()),
            new Clock(),
            telemetry,
            FakeOrg.LenientOrg().Lookup());
    }

    [Fact]
    public async Task Accepted_and_refused_proposals_are_counted_per_tool()
    {
        using var telemetry = new AgentTelemetry();
        using var capture = new Capture(telemetry);
        var tools = Tools(telemetry);

        await tools.ProposeGrant(Guid.NewGuid(), 10m, "thanks");
        await tools.ProposeGrant(Guid.NewGuid(), 10m, "thanks");
        await Assert.ThrowsAsync<McpException>(() => tools.ProposeGrant(Guid.NewGuid(), 99999m, "too much"));
        await Assert.ThrowsAsync<McpException>(() => tools.ProposeHire("Anna\nX", "a@x.test", Dept, Pos));

        Assert.Equal(2, capture.Sum("agent_proposals", ("gen_ai.tool.name", "propose_grant_currency"), ("outcome", "proposed")));
        Assert.Equal(1, capture.Sum("agent_proposals", ("gen_ai.tool.name", "propose_grant_currency"), ("outcome", "refused")));
        Assert.Equal(1, capture.Sum("agent_proposals", ("gen_ai.tool.name", "propose_hire_employee"), ("outcome", "refused")));
    }

    [Fact]
    public async Task A_proposal_without_a_signed_in_user_counts_as_refused()
    {
        using var telemetry = new AgentTelemetry();
        using var capture = new Capture(telemetry);

        await Assert.ThrowsAsync<McpException>(() => Tools(telemetry, signedIn: false).ProposeGrant(Guid.NewGuid(), 10m, "x"));

        Assert.Equal(1, capture.Sum("agent_proposals", ("outcome", "refused")));
    }

    [Fact]
    public async Task Steps_are_counted_by_kind_and_outcome_including_the_ones_that_never_ran()
    {
        using var telemetry = new AgentTelemetry();
        using var capture = new Capture(telemetry);
        var employees = new RecordingService((_, _) => RecordingService.Json(HttpStatusCode.Forbidden, "{}"));
        var executor = ExecutorWith(employees, new RecordingService((_, _) => RecordingService.Ok(Guid.NewGuid())), telemetry);

        await executor.ExecuteAsync(PlanOf(Hire(), GrantToHired()), CancellationToken.None);

        Assert.Equal(1, capture.Sum("agent_steps", ("kind", "HireEmployee"), ("outcome", "Failed")));
        Assert.Equal(1, capture.Sum("agent_steps", ("kind", "GrantCurrency"), ("outcome", "NotRun")));
        Assert.Equal(0, capture.Sum("agent_steps", ("outcome", "Applied")));
    }

    [Fact]
    public async Task A_run_leaves_one_span_for_the_plan_and_one_per_step_that_actually_ran()
    {
        using var telemetry = new AgentTelemetry();
        using var capture = new Capture(telemetry);
        var executor = ExecutorWith(
            new RecordingService((_, _) => RecordingService.Ok(Guid.NewGuid())),
            new RecordingService((_, _) => RecordingService.Ok(Guid.NewGuid())),
            telemetry);
        var plan = PlanOf(Hire(), GrantToHired());

        await executor.ExecuteAsync(plan, CancellationToken.None);

        Assert.Equal(2, capture.Activities.Count(a => a.OperationName == "agent.step"));
        var run = Assert.Single(capture.Activities, a => a.OperationName == "agent.execute");
        Assert.Equal(plan.Id.ToString(), run.GetTagItem("agent.plan_id"));
        Assert.All(capture.Activities.Where(a => a.OperationName == "agent.step"), a => Assert.Equal("Applied", a.GetTagItem("agent.step.outcome")));
    }

    [Fact]
    public async Task No_span_or_metric_carries_the_plan_token_or_anything_a_user_typed()
    {
        using var telemetry = new AgentTelemetry();
        using var capture = new Capture(telemetry);
        var tools = Tools(telemetry);
        var proposal = await tools.ProposeHire("Secret Name Person", "secret.mail@x.test", Dept, Pos);
        var executor = ExecutorWith(
            new RecordingService((_, _) => RecordingService.Ok(Guid.NewGuid())),
            new RecordingService((_, _) => RecordingService.Ok(Guid.NewGuid())),
            telemetry);
        await executor.ExecuteAsync(PlanOf(Hire() with { FullName = "Secret Name Person", Email = "secret.mail@x.test" }, GrantToHired()), CancellationToken.None);

        var everything = string.Join(
            " ",
            capture.Activities.SelectMany(a => a.TagObjects).Select(t => $"{t.Key}={t.Value}")
                .Concat(capture.Measurements.SelectMany(m => m.Tags).Select(t => $"{t.Key}={t.Value}")));

        Assert.DoesNotContain("Secret", everything);
        Assert.DoesNotContain("secret", everything);
        Assert.DoesNotContain(proposal.PlanToken, everything);
    }

    private static PlanExecutor ExecutorWith(RecordingService employees, RecordingService rewards, AgentTelemetry telemetry)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = CallerToken;
        var accessor = new HttpContextAccessor { HttpContext = context };
        HttpClient Client(HttpMessageHandler inner) => new(new McpServer.Api.BearerForwardingHandler(accessor) { InnerHandler = inner })
        {
            BaseAddress = new Uri("http://service.test/"),
        };

        return new PlanExecutor(
            new McpServer.Api.EmployeeCommandClient(Client(employees)),
            new McpServer.Api.RewardsCommandClient(Client(rewards)),
            new AgentOptions(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PlanExecutor>.Instance,
            telemetry);
    }
}
