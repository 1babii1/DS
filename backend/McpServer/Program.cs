using McpServer.Api;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Shared.Observability;
using Shared.Security;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddObservability(builder.Configuration, "mcp-server");

builder.Services.AddHttpContextAccessor();
builder.Services.AddTransient<BearerForwardingHandler>();

// Every tool talks to a service's own API as the caller (BearerForwardingHandler). McpServer has no
// database connection and no credential of its own: what a tool can read is exactly what the
// person using it could read directly.
builder.Services.AddHttpClient<DirectoryApiClient>(client =>
        client.BaseAddress = new Uri(builder.Configuration["Services:Directory:BaseUrl"]
            ?? throw new InvalidOperationException("Configuration 'Services:Directory:BaseUrl' is not set.")))
    .AddHttpMessageHandler<BearerForwardingHandler>()
    .AddResilienceHandler("directory-api", ServiceApiResilience.Configure);

builder.Services.AddHttpClient<EmployeeApiClient>(client =>
        client.BaseAddress = new Uri(builder.Configuration["Services:Employee:BaseUrl"]
            ?? throw new InvalidOperationException("Configuration 'Services:Employee:BaseUrl' is not set.")))
    .AddHttpMessageHandler<BearerForwardingHandler>()
    .AddResilienceHandler("employee-api", ServiceApiResilience.Configure);

// Inbound: the same tokens and the same JWKS as every other service. The token that authenticates a
// request here is also what BearerForwardingHandler passes on, so this is the one place the caller's
// identity is established for everything a tool then reads.
builder.Services.AddPlatformJwtAuthentication(builder.Configuration, builder.Environment);

builder.Services.AddAuthorization();

builder.Services.AddHealthChecks();

builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapMcp("/mcp").RequireAuthorization();

// As elsewhere: liveness checks nothing (a restart does not fix an unreachable dependency);
// readiness runs whatever is tagged "ready" - currently nothing, as McpServer owns no database.
app.MapHealthChecks("/health/live", new() { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new() { Predicate = c => c.Tags.Contains("ready") }).AllowAnonymous();

app.Run();

namespace McpServer
{
    public partial class Program;
}