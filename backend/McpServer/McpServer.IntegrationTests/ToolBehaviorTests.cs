using System.Net;
using System.Text;
using McpServer.Api;
using McpServer.Tools;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol;
using Shared;

namespace McpServer.IntegrationTests;

// The tools are exercised with the real forwarding handler and the real clients, over a stub that
// stands in for DirectoryService/EmployeeService: what is under test is what leaves McpServer (whose
// credentials, which URL, how much data) and what a failure exposes, not the services themselves.
public class ToolBehaviorTests
{
    private const string CallerToken = "Bearer caller-token-123";

    private sealed class StubService(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static DirectoryTools Tools(StubService stub, string? incomingAuthorization = CallerToken)
    {
        var context = new DefaultHttpContext();
        if (incomingAuthorization is not null)
        {
            context.Request.Headers.Authorization = incomingAuthorization;
        }

        var accessor = new HttpContextAccessor { HttpContext = context };
        HttpClient Client() => new(new BearerForwardingHandler(accessor) { InnerHandler = stub })
        {
            BaseAddress = new Uri("http://service.test/"),
        };

        return new DirectoryTools(new DirectoryApiClient(Client()), new EmployeeApiClient(Client()));
    }

    private static readonly Guid Dept = Guid.NewGuid();

    // ---- whose credentials leave McpServer ------------------------------------------------

    [Fact]
    public async Task The_callers_own_token_is_what_reaches_the_service()
    {
        var stub = new StubService(_ => Json("""{"items":[],"page":1,"size":20,"total":0}"""));

        await Tools(stub).ListEmployeesByDepartment(Dept);

        var sent = Assert.Single(stub.Requests);
        Assert.Equal(CallerToken, sent.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task With_no_caller_token_nothing_is_sent_at_all()
    {
        var stub = new StubService(_ => Json("""{"result":[],"isError":false}"""));
        var tools = Tools(stub, incomingAuthorization: null);

        var ex = await Assert.ThrowsAsync<McpException>(() => tools.SearchDepartments("payments"));

        Assert.Empty(stub.Requests);
        Assert.Equal("Your session is not valid for this service.", ex.Message);
    }

    // ---- what a failure exposes -------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "Your session is not valid for this service.")]
    [InlineData(HttpStatusCode.Forbidden, "You are not allowed to do this.")]
    [InlineData(HttpStatusCode.TooManyRequests, "Too many requests, try again shortly.")]
    [InlineData(HttpStatusCode.InternalServerError, "The service could not complete the request.")]
    public async Task A_failed_call_surfaces_a_fixed_message_and_never_the_response_body(
        HttpStatusCode status, string expected)
    {
        var stub = new StubService(_ => Json("""{"secret":"SECRET-DETAIL-do-not-leak"}""", status));

        var ex = await Assert.ThrowsAsync<McpException>(() => Tools(stub).SearchDepartments("payments"));

        Assert.Equal(expected, ex.Message);
        Assert.DoesNotContain("SECRET-DETAIL", ex.ToString().Replace(ex.InnerException?.ToString() ?? "", string.Empty));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Every_tool_maps_failures_to_a_tool_error_not_data(HttpStatusCode status)
    {
        var stub = new StubService(_ => Json("""{"secret":"SECRET-DETAIL"}""", status));
        var tools = Tools(stub);
        var id = Guid.NewGuid();

        await Assert.ThrowsAsync<McpException>(() => tools.ListEmployeesByDepartment(id));
        await Assert.ThrowsAsync<McpException>(() => tools.GetDepartmentTree(id));
        await Assert.ThrowsAsync<McpException>(() => tools.GetDepartmentTree());
        await Assert.ThrowsAsync<McpException>(() => tools.SearchDepartments("x"));
        if (status != HttpStatusCode.NotFound)
        {
            await Assert.ThrowsAsync<McpException>(() => tools.GetEmployee(id));
        }
    }

    [Fact]
    public async Task Paging_arguments_are_clamped_before_they_reach_the_service()
    {
        var stub = new StubService(_ => Json("""{"items":[],"page":1,"size":200,"total":0}"""));

        await Tools(stub).ListEmployeesByDepartment(Dept, page: int.MaxValue, size: int.MaxValue);
        await Tools(stub).ListEmployeesByDepartment(Dept, page: -5, size: -5);

        var first = stub.Requests[0].RequestUri!.Query;
        var second = stub.Requests[1].RequestUri!.Query;
        Assert.Contains("page=100000", first);
        Assert.Contains("size=200", first);
        Assert.Contains("page=1&", second);
        Assert.Contains("size=1", second);
    }

    [Fact]
    public async Task An_unknown_employee_is_null_not_an_error()
    {
        var stub = new StubService(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        Assert.Null(await Tools(stub).GetEmployee(Guid.NewGuid()));
    }

    // ---- how much data one call can return --------------------------------------------------

    [Fact]
    public async Task Listing_employees_asks_for_one_bounded_page_never_the_whole_department()
    {
        var stub = new StubService(_ => Json("""{"items":[],"page":1,"size":20,"total":0}"""));

        await Tools(stub).ListEmployeesByDepartment(Dept);

        var url = Assert.Single(stub.Requests).RequestUri!.PathAndQuery;
        Assert.Contains($"departmentId={Dept}", url);
        Assert.Contains("page=1", url);
        Assert.Contains("size=20", url);
    }

    [Fact]
    public async Task A_page_carries_the_paging_facts_so_the_model_can_ask_for_the_next_one()
    {
        var stub = new StubService(_ => Json($$"""
            {"items":[{"id":"{{Guid.NewGuid()}}","fullName":"Ann A","email":"a@x.test","departmentId":"{{Dept}}",
              "departmentName":"Payments","positionId":"{{Guid.NewGuid()}}","positionName":"Dev","status":"Active",
              "provisioningFailureReason":null,"hiredAt":"2026-01-02T03:04:05Z"}],"page":1,"size":1,"total":3}
            """));

        var page = await Tools(stub).ListEmployeesByDepartment(Dept, page: 1, size: 1);

        var ann = Assert.Single(page.Items);
        Assert.Equal("Ann A", ann.FullName);
        Assert.Equal("Payments", ann.DepartmentName);
        Assert.Equal(3, page.Total);
        Assert.True(page.HasNext);
    }

    // ---- shape parity with what the tools returned before ------------------------------------

    [Fact]
    public async Task A_subtree_is_read_in_one_call_and_maps_to_nodes_with_the_cut_flag()
    {
        var root = Guid.NewGuid();
        var child = Guid.NewGuid();
        var stub = new StubService(_ => Json($$"""
            {"result":{"nodes":[
                {"id":"{{root}}","parentId":null,"name":"Root","identifier":"root","depth":0,"isActive":true},
                {"id":"{{child}}","parentId":"{{root}}","name":"A","identifier":"root.a","depth":1,"isActive":true}
            ],"truncated":true},"isError":false}
            """));

        var tree = await Tools(stub).GetDepartmentTree(root);

        Assert.Single(stub.Requests);
        Assert.Equal($"/api/departments/{root}/subtree", stub.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal(["Root", "A"], tree.Nodes.Select(n => n.Name).ToArray());
        Assert.Null(tree.Nodes[0].ParentId);
        Assert.Equal(root, tree.Nodes[1].ParentId);
        Assert.Equal((short)1, tree.Nodes[1].Depth);
        Assert.True(tree.HasMore);
    }

    [Fact]
    public async Task An_unknown_department_is_an_empty_tree()
    {
        var stub = new StubService(_ => Json("""{"result":{"nodes":[],"truncated":false},"isError":false}"""));

        var tree = await Tools(stub).GetDepartmentTree(Guid.NewGuid());

        Assert.Empty(tree.Nodes);
        Assert.False(tree.HasMore);
    }

    [Fact]
    public async Task Root_departments_skip_inactive_ones_and_report_a_full_page_as_possibly_more()
    {
        var items = string.Join(",", Enumerable.Range(0, 20).Select(i =>
            $$"""{"id":"{{Guid.NewGuid()}}","parentId":null,"name":"D{{i:00}}","identifier":"d{{i}}","depth":0,"isActive":{{(i == 3 ? "false" : "true")}}}"""));
        var stub = new StubService(_ => Json($$"""{"result":[{{items}}],"isError":false}"""));

        var tree = await Tools(stub).GetDepartmentTree(page: 2);

        Assert.Contains("page=2", stub.Requests[0].RequestUri!.Query);
        Assert.Equal(19, tree.Nodes.Count);
        Assert.DoesNotContain(tree.Nodes, n => n.Name == "D03");
        Assert.True(tree.HasMore);
    }

    [Fact]
    public async Task A_short_page_of_roots_has_no_more()
    {
        var stub = new StubService(_ => Json($$"""{"result":[{"id":"{{Guid.NewGuid()}}","parentId":null,"name":"Only","identifier":"only","depth":0,"isActive":true}],"isError":false}"""));

        Assert.False((await Tools(stub).GetDepartmentTree()).HasMore);
    }

    [Fact]
    public async Task Search_escapes_the_query_and_clamps_the_limit()
    {
        var stub = new StubService(_ => Json("""{"result":[{"id":"00000000-0000-0000-0000-000000000001","name":"Payments","identifier":"pay","score":0.87}],"isError":false}"""));

        var results = await Tools(stub).SearchDepartments("teams & payments?", limit: 500);

        var url = Assert.Single(stub.Requests).RequestUri!.PathAndQuery;
        Assert.Contains("query=teams%20%26%20payments%3F", url);
        Assert.Contains("limit=50", url);
        Assert.Equal(0.87, Assert.Single(results).Score);
    }

    // ---- no database ------------------------------------------------------------------------

    [Fact]
    public void McpServer_has_no_code_that_reaches_a_database()
    {
        var referenced = typeof(McpServer.Api.DirectoryApiClient).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .ToList();

        // Referenced assemblies are only those the compiled code actually uses, so this catches a
        // tool or client that opens a connection even though Shared drags the packages in transitively.
        Assert.DoesNotContain(referenced, n =>
            n.StartsWith("Npgsql", StringComparison.Ordinal)
            || n.StartsWith("Dapper", StringComparison.Ordinal)
            || n.StartsWith("Pgvector", StringComparison.Ordinal)
            || n.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
    }
}
