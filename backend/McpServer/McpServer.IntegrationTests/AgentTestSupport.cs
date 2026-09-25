using System.Net;
using System.Text;
using McpServer.Agent;
using McpServer.Api;
using Microsoft.AspNetCore.Http;

namespace McpServer.IntegrationTests;

internal sealed record Seen(HttpMethod Method, string Path, string? Authorization, string? IdempotencyKey, string Body);

// Stands in for EmployeeService / RewardsService. It records what left McpServer - including the body and the
// headers - because the properties under test are about exactly that: whose token, which key, which employee.
internal sealed class RecordingService(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<Seen> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
        var key = request.Headers.TryGetValues("Idempotency-Key", out var values) ? values.Single() : null;
        Requests.Add(new Seen(request.Method, request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString(), key, body));
        return respond(request, body);
    }

    public static HttpResponseMessage Ok(Guid? id = null) => Json(
        HttpStatusCode.OK, id is null ? """{"isError":false}""" : $$"""{"result":"{{id}}","isError":false}""");

    public static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

internal static class AgentTestSupport
{
    public const string CallerToken = "Bearer caller-token-123";

    public static readonly Guid Dept = Guid.NewGuid();
    public static readonly Guid Pos = Guid.NewGuid();

    public static PlanExecutor Executor(
        RecordingService employees, RecordingService rewards, string? incomingAuthorization = CallerToken, decimal maxGrant = 1000m)
    {
        var context = new DefaultHttpContext();
        if (incomingAuthorization is not null)
        {
            context.Request.Headers.Authorization = incomingAuthorization;
        }

        var accessor = new HttpContextAccessor { HttpContext = context };
        HttpClient Client(HttpMessageHandler inner) => new(new BearerForwardingHandler(accessor) { InnerHandler = inner })
        {
            BaseAddress = new Uri("http://service.test/"),
        };

        return new PlanExecutor(
            new EmployeeCommandClient(Client(employees)),
            new RewardsCommandClient(Client(rewards)),
            new AgentOptions { MaxGrantAmount = maxGrant },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PlanExecutor>.Instance);
    }

    public static PlanStep Hire() => new(
        StepKind.HireEmployee, "Hire Anna", FullName: "Anna Ivanova", Email: "anna@x.test", DepartmentId: Dept, PositionId: Pos);

    public static PlanStep GrantToHired(decimal amount = 500m, int from = 0) => new(
        StepKind.GrantCurrency, "Grant", EmployeeFromStep: from, Amount: amount, Reason: "spot bonus");

    public static Plan PlanOf(params PlanStep[] steps) => new(
        Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(10), steps);
}
