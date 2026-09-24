using System.Net;
using McpServer.Api;
using McpServer.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using ModelContextProtocol;
using Polly;

namespace McpServer.IntegrationTests;

public class ResilienceAndWiringTests
{
    private sealed class CountingStub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private int _calls;

        public int Calls => _calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpClient ClientWithRealPolicy(CountingStub stub)
    {
        var builder = new ResiliencePipelineBuilder<HttpResponseMessage>();
        ServiceApiResilience.Configure(builder);
        return new HttpClient(new ResilienceHandler(builder.Build()) { InnerHandler = stub })
        {
            BaseAddress = new Uri("http://service.test/"),
        };
    }

    // ---- the real policy, counted --------------------------------------------------------------

    [Fact]
    public async Task A_server_error_is_retried_twice_then_reported()
    {
        var stub = new CountingStub(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var client = ClientWithRealPolicy(stub);

        using var response = await client.GetAsync("x");

        Assert.Equal(3, stub.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Client_side_answers_are_never_retried(HttpStatusCode status)
    {
        var stub = new CountingStub(_ => new HttpResponseMessage(status));
        using var client = ClientWithRealPolicy(stub);

        using var response = await client.GetAsync("x");

        Assert.Equal(1, stub.Calls);
    }

    [Fact]
    public async Task Rate_limit_answers_do_not_open_the_breaker_for_everyone()
    {
        var stub = new CountingStub(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        using var client = ClientWithRealPolicy(stub);

        for (var i = 0; i < 30; i++)
        {
            using var response = await client.GetAsync("x");
            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        }

        Assert.Equal(30, stub.Calls);
    }

    [Fact]
    public async Task Repeated_server_errors_open_the_breaker_and_it_surfaces_as_the_fixed_message()
    {
        var stub = new CountingStub(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var http = ClientWithRealPolicy(stub);
        var tools = new DirectoryTools(new DirectoryApiClient(http), new EmployeeApiClient(http));

        McpException? opened = null;
        for (var i = 0; i < 8 && opened is null; i++)
        {
            try
            {
                await tools.SearchDepartments("x");
            }
            catch (McpException ex) when (ex.Message == "The service could not be reached.")
            {
                opened = ex;
            }
            catch (McpException)
            {
                // still just failing; keep going until the breaker opens
            }
        }

        Assert.NotNull(opened);
    }

    // ---- the production wiring ------------------------------------------------------------------

    [Fact]
    public async Task The_real_dependency_injection_setup_forwards_the_callers_token_and_only_theirs()
    {
        var seen = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        var stub = new CountingStub(request =>
        {
            seen[request.RequestUri!.AbsolutePath] = request.Headers.Authorization?.ToString() ?? "<none>";
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureTestServices(services =>
            {
                // Replaces only the innermost handler: the forwarding handler and the resilience
                // handler that Program.cs registered stay in the chain being tested.
                services.AddHttpClient<EmployeeApiClient>().ConfigurePrimaryHttpMessageHandler(() => stub);
            }));

        // Many callers at once, each with a different token: the token seen for each employee id must
        // be that caller's own, never a neighbor's.
        var callers = Enumerable.Range(0, 40).Select(i => (Id: Guid.NewGuid(), Token: $"Bearer token-{i}")).ToList();

        await Task.WhenAll(callers.Select(caller => Task.Run(async () =>
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
            var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            context.Request.Headers.Authorization = caller.Token;
            accessor.HttpContext = context;

            var tools = ActivatorUtilities.CreateInstance<DirectoryTools>(scope.ServiceProvider);
            await tools.GetEmployee(caller.Id);
        })));

        Assert.Equal(callers.Count, seen.Count);
        foreach (var caller in callers)
        {
            Assert.Equal(caller.Token, seen[$"/api/employees/{caller.Id}"]);
        }
    }
}
