using System.Net;
using System.Text;
using System.Text.Json;
using McpServer.Agent;
using McpServer.Api;
using Microsoft.AspNetCore.Http;

namespace McpServer.IntegrationTests;

// Stands in for DirectoryService and EmployeeService reads. Strict by default: only what is added exists, so a
// test can show an unknown or wrong-kind id being refused. Lenient answers any id with a plausible record, for
// tests about something else that just need the lookups to succeed.
internal sealed class FakeOrg : HttpMessageHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Dictionary<Guid, Emp> Employees { get; } = [];

    public Dictionary<Guid, (string Name, bool Active)> Departments { get; } = [];

    public Dictionary<Guid, List<(Guid Id, string Name)>> PositionsOf { get; } = [];

    public bool Lenient { get; init; }

    public bool Down { get; set; }

    public List<(string PathAndQuery, string? Authorization)> Requests { get; } = [];

    public static FakeOrg LenientOrg() => new() { Lenient = true };

    public FakeOrg WithDepartment(Guid id, string name, bool active = true, params (Guid Id, string Name)[] positions)
    {
        Departments[id] = (name, active);
        PositionsOf[id] = [.. positions];
        return this;
    }

    public FakeOrg WithEmployee(Emp employee)
    {
        Employees[employee.Id] = employee;
        return this;
    }

    public PlanLookup Lookup(string authorization = AgentTestSupport.CallerToken)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = authorization;
        // Not HttpContextAccessor: it keeps the context in a static AsyncLocal, so a second one would replace the
        // signed-in user that the tools under test read through their own accessor.
        var accessor = new FixedAccessor(context);
        HttpClient Client() => new(new BearerForwardingHandler(accessor) { InnerHandler = this })
        {
            BaseAddress = new Uri("http://service.test/"),
        };
        return new PlanLookup(new DirectoryApiClient(Client()), new EmployeeApiClient(Client()));
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add((request.RequestUri!.PathAndQuery, request.Headers.Authorization?.ToString()));
        if (Down)
        {
            throw new HttpRequestException("down");
        }

        var path = request.RequestUri.AbsolutePath.Trim('/');
        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);

        if (path.StartsWith("api/employees/", StringComparison.Ordinal) && Guid.TryParse(path[14..], out var employeeId))
        {
            var employee = Employees.GetValueOrDefault(employeeId) ?? (Lenient ? LenientEmployee(employeeId) : null);
            return Task.FromResult(employee is null ? Reply(HttpStatusCode.NotFound, "{}") : Reply(HttpStatusCode.OK, Serialise(new
            {
                id = employee.Id,
                fullName = employee.Name,
                email = employee.Email,
                departmentId = employee.DeptId,
                departmentName = employee.DeptName,
                positionId = employee.PosId,
                positionName = employee.PosName,
                status = employee.Status,
                hiredAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            })));
        }

        if (path.StartsWith("api/departments/department/", StringComparison.Ordinal)
            && Guid.TryParse(path["api/departments/department/".Length..], out var departmentId))
        {
            var known = Departments.TryGetValue(departmentId, out var department);
            if (!known && Lenient)
            {
                department = ("Lenient department", true);
                known = true;
            }

            return Task.FromResult(Reply(HttpStatusCode.OK, known
                ? Serialise(new { result = new { id = departmentId, name = department.Name, isActive = department.Active }, isError = false })
                : """{"result":null,"isError":false}"""));
        }

        if (path == "api/positions" && Guid.TryParse(query["departmentId"], out var positionsDepartment))
        {
            var positions = PositionsOf.TryGetValue(positionsDepartment, out var list)
                ? list
                : Lenient ? [(AgentTestSupport.Pos, "Lenient position")] : [];
            return Task.FromResult(Reply(HttpStatusCode.OK, Serialise(new
            {
                items = positions.Select(p => new { id = p.Id, name = p.Name, isActive = true }),
                page = 1,
                size = 200,
                total = positions.Count,
            })));
        }

        return Task.FromResult(Reply(HttpStatusCode.NotFound, "{}"));
    }

    private static Emp LenientEmployee(Guid id) =>
        new(id, "Lenient Person", "lenient@x.test", AgentTestSupport.Dept, "Lenient department", AgentTestSupport.Pos, "Lenient position");

    private static string Serialise(object value) => JsonSerializer.Serialize(value, Json);

    private static HttpResponseMessage Reply(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class FixedAccessor(HttpContext context) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = context;
    }

    internal sealed record Emp(
        Guid Id, string Name, string Email, Guid DeptId, string DeptName, Guid PosId, string PosName, string Status = "Active");
}
