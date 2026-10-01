using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using McpServer.Api;
using McpServer.Tools;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace McpServer.IntegrationTests;

// The history tool over the real forwarding handler and client, with a stub standing in for AuditService: what is
// under test is what leaves McpServer (whose token, which date, what was validated first), how much comes back, and
// what a failure exposes.
public class OrgHistoryToolTests
{
    private const string CallerToken = "Bearer caller-token-123";
    private static readonly Guid Payments = Guid.NewGuid();
    private static readonly Guid Engineering = Guid.NewGuid();

    private sealed class StubService(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    private static object Person(string name, string position) => new { id = Guid.NewGuid(), name, position };

    private static object Chart(object[] departments, object[]? unplaced = null) => new
    {
        at = new DateTime(2026, 3, 31, 23, 59, 59, DateTimeKind.Utc),
        firstEventAt = new DateTime(2026, 1, 5, 9, 0, 0, DateTimeKind.Utc),
        departments,
        unplaced = unplaced ?? [],
        skippedEvents = 0,
    };

    private static object Dept(Guid id, string name, Guid? parent, int depth, params object[] people) =>
        new { id, parentId = parent, name, identifier = name.ToLowerInvariant(), depth, people };

    private static OrgHistoryTools Tools(StubService stub, string? incomingAuthorization = CallerToken)
    {
        var context = new DefaultHttpContext();
        if (incomingAuthorization is not null)
        {
            context.Request.Headers.Authorization = incomingAuthorization;
        }

        var accessor = new HttpContextAccessor { HttpContext = context };
        return new OrgHistoryTools(new AuditApiClient(new HttpClient(new BearerForwardingHandler(accessor) { InnerHandler = stub })
        {
            BaseAddress = new Uri("http://service.test/"),
        }));
    }

    private static StubService Answering(object chart) => new(_ => Json(chart));

    [Fact]
    public async Task The_callers_own_token_and_the_date_as_asked_are_what_reach_the_service()
    {
        var stub = Answering(Chart([]));

        await Tools(stub).GetOrgSnapshot("2026-03-31");

        var sent = Assert.Single(stub.Requests);
        Assert.Equal(CallerToken, sent.Headers.Authorization!.ToString());
        Assert.Equal("/api/audit/org-chart", sent.RequestUri!.AbsolutePath);
        Assert.Equal("?at=2026-03-31", sent.RequestUri.Query);
    }

    [Fact]
    public async Task A_time_is_sent_escaped_so_it_cannot_add_a_parameter()
    {
        var stub = Answering(Chart([]));

        await Tools(stub).GetOrgSnapshot("2026-03-05T10:00:00+02:00");

        Assert.Equal("?at=2026-03-05T10%3A00%3A00%2B02%3A00", Assert.Single(stub.Requests).RequestUri!.Query);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("last March")]
    [InlineData("2026-03-05&admin=true")]
    [InlineData("1999-01-01")]
    public async Task A_value_that_cannot_be_a_date_is_refused_with_what_is_expected_and_nothing_is_sent(string at)
    {
        var stub = Answering(Chart([]));

        var ex = await Assert.ThrowsAsync<McpException>(() => Tools(stub).GetOrgSnapshot(at));

        Assert.Empty(stub.Requests);
        Assert.Contains("2026-03-05", ex.Message);
    }

    [Fact]
    public async Task With_no_caller_token_nothing_is_sent_at_all()
    {
        var stub = Answering(Chart([]));

        var ex = await Assert.ThrowsAsync<McpException>(() => Tools(stub, incomingAuthorization: null).GetOrgSnapshot("2026-03-31"));

        Assert.Empty(stub.Requests);
        Assert.Equal("Your session is not valid for this service.", ex.Message);
    }

    [Fact]
    public async Task Without_a_department_it_lists_the_days_departments_with_headcount_and_no_people()
    {
        var stub = Answering(Chart(
            [
                Dept(Engineering, "Engineering", null, 0, Person("Ada", "Team Lead"), Person("Grace", "Developer")),
                Dept(Payments, "Payments", Engineering, 1, Person("Alan", "Developer")),
            ],
            [Person("Orphan", "Developer")]));

        var result = await Tools(stub).GetOrgSnapshot("2026-03-31");

        Assert.Equal(2, result.Departments!.Count);
        Assert.Equal(2, result.Departments.Single(d => d.Name == "Engineering").Headcount);
        Assert.Equal(Engineering, result.Departments.Single(d => d.Name == "Payments").ParentId);
        Assert.Null(result.Department);
        Assert.Equal(1, result.Unplaced);
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("Ada", serialized);
        Assert.DoesNotContain("Alan", serialized);
    }

    [Fact]
    public async Task With_a_department_it_lists_who_worked_there_on_that_date_and_only_there()
    {
        var stub = Answering(Chart(
        [
            Dept(Engineering, "Engineering", null, 0, Person("Ada", "Team Lead")),
            Dept(Payments, "Payments", null, 0, Person("Alan", "Developer"), Person("Linus", "Team Lead")),
        ]));

        var result = await Tools(stub).GetOrgSnapshot("2026-03-31", Payments);

        var department = result.Department!;
        Assert.Equal("Payments", department.Name);
        Assert.Equal(["Alan/Developer", "Linus/Team Lead"], department.People.Select(p => $"{p.Name}/{p.Position}").ToArray());
        Assert.Null(result.Departments);
        Assert.DoesNotContain("Ada", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task A_department_that_did_not_exist_on_that_date_is_said_so_not_an_error_and_not_someone_elses_data()
    {
        var stub = Answering(Chart([Dept(Engineering, "Engineering", null, 0, Person("Ada", "Team Lead"))]));

        var result = await Tools(stub).GetOrgSnapshot("2026-03-31", Guid.NewGuid());

        Assert.Null(result.Department);
        Assert.Contains("No department with that id", result.Note);
    }

    [Fact]
    public async Task An_empty_history_says_that_nothing_had_been_recorded()
    {
        var result = await Tools(Answering(Chart([]))).GetOrgSnapshot("2026-01-01");

        Assert.Empty(result.Departments!);
        Assert.Contains("Nothing had been recorded", result.Note);
    }

    [Fact]
    public async Task One_call_cannot_return_an_unbounded_slice_of_the_directory()
    {
        var many = Enumerable.Range(0, OrgHistoryTools.MaxDepartments + 5)
            .Select(i => Dept(Guid.NewGuid(), $"Dept {i:000}", null, 0))
            .ToArray();
        var crowded = Dept(Payments, "Payments", null, 0,
            Enumerable.Range(0, OrgHistoryTools.MaxPeople + 5).Select(i => Person($"Person {i:000}", "Developer")).ToArray());

        var departments = await Tools(Answering(Chart(many))).GetOrgSnapshot("2026-03-31");
        var people = await Tools(Answering(Chart([crowded]))).GetOrgSnapshot("2026-03-31", Payments);

        Assert.Equal(OrgHistoryTools.MaxDepartments, departments.Departments!.Count);
        Assert.True(departments.DepartmentsTruncated);
        Assert.Equal(OrgHistoryTools.MaxPeople, people.Department!.People.Count);
        Assert.True(people.Department.PeopleTruncated);
        Assert.Equal(OrgHistoryTools.MaxPeople + 5, people.Department.Headcount);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "Your session is not valid for this service.")]
    [InlineData(HttpStatusCode.Forbidden, "You are not allowed to do this.")]
    [InlineData(HttpStatusCode.TooManyRequests, "Too many requests, try again shortly.")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "The service rejected the request as invalid.")]
    [InlineData(HttpStatusCode.InternalServerError, "The service could not complete the request.")]
    public async Task A_failed_call_surfaces_a_fixed_message_and_never_the_response_body(HttpStatusCode status, string expected)
    {
        var stub = new StubService(_ => Json(new { detail = "SECRET-DETAIL-do-not-leak" }, status));

        var ex = await Assert.ThrowsAsync<McpException>(() => Tools(stub).GetOrgSnapshot("2026-03-31"));

        Assert.Equal(expected, ex.Message);
        Assert.DoesNotContain("SECRET-DETAIL", ex.Message);
    }

    [Fact]
    public void The_tool_is_offered_by_name_and_is_a_read_not_a_proposal()
    {
        var names = typeof(OrgHistoryTools).GetMethods()
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .Where(n => n is not null)
            .ToArray();

        Assert.Equal(["get_org_snapshot"], names);
    }
}
