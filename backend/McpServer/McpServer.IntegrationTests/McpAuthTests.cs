using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace McpServer.IntegrationTests;

/// <summary>
/// Only the one property this session's audit specifically fixed and verified live
/// against a running stack: /mcp rejects a request with no valid token before it ever
/// reaches a tool. Deliberately narrow - proving real tool behavior (search results,
/// employee lookups) would mean replicating DirectoryService's and EmployeeService's
/// schemas in this test project, which is a source of drift, not a source of
/// confidence. That coverage belongs in those services' own test suites, exercising
/// the same tables through the code that owns them.
///
/// No Testcontainers here: authentication is rejected in ASP.NET's own middleware
/// pipeline before a request ever reaches a tool method, so nothing in these two
/// tests ever touches Postgres. The connection string in appsettings.Development.json
/// stays unreachable for the whole run and that's fine - the assertion never gets
/// far enough to need it.
/// </summary>
public class McpAuthTests
{
    [Fact]
    public async Task Mcp_endpoint_without_a_token_is_unauthorized()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "tools/list" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Mcp_endpoint_with_a_garbage_token_is_unauthorized()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", "Bearer not-a-real-token");

        var response = await client.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "tools/list" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}