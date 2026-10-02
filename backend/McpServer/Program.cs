using McpServer.Agent;
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

builder.Services.AddHttpClient<SearchApiClient>(client =>
        client.BaseAddress = new Uri(builder.Configuration["Services:Search:BaseUrl"]
            ?? throw new InvalidOperationException("Configuration 'Services:Search:BaseUrl' is not set.")))
    .AddHttpMessageHandler<BearerForwardingHandler>()
    .AddResilienceHandler("search-api", ServiceApiResilience.Configure);

builder.Services.AddHttpClient<AuditApiClient>(client =>
        client.BaseAddress = new Uri(builder.Configuration["Services:Audit:BaseUrl"]
            ?? throw new InvalidOperationException("Configuration 'Services:Audit:BaseUrl' is not set.")))
    .AddHttpMessageHandler<BearerForwardingHandler>()
    .AddResilienceHandler("audit-api", ServiceApiResilience.Configure);

builder.Services.AddHttpClient<EmployeeApiClient>(client =>
        client.BaseAddress = new Uri(builder.Configuration["Services:Employee:BaseUrl"]
            ?? throw new InvalidOperationException("Configuration 'Services:Employee:BaseUrl' is not set.")))
    .AddHttpMessageHandler<BearerForwardingHandler>()
    .AddResilienceHandler("employee-api", ServiceApiResilience.Configure);

// The write side: what the user has approved is run through the same APIs, as the same caller. Separate
// clients with no retry (see CommandClients).
builder.Services.AddHttpClient<EmployeeCommandClient>(client =>
        client.BaseAddress = new Uri(builder.Configuration["Services:Employee:BaseUrl"]
            ?? throw new InvalidOperationException("Configuration 'Services:Employee:BaseUrl' is not set.")))
    .AddHttpMessageHandler<BearerForwardingHandler>()
    .AddResilienceHandler("employee-commands", ServiceApiResilience.ConfigureForWrites);

builder.Services.AddHttpClient<RewardsCommandClient>(client =>
        client.BaseAddress = new Uri(builder.Configuration["Services:Rewards:BaseUrl"]
            ?? throw new InvalidOperationException("Configuration 'Services:Rewards:BaseUrl' is not set.")))
    .AddHttpMessageHandler<BearerForwardingHandler>()
    .AddResilienceHandler("rewards-commands", ServiceApiResilience.ConfigureForWrites);

var agentOptions = builder.Configuration.GetSection(AgentOptions.SectionName).Get<AgentOptions>() ?? new AgentOptions();
builder.Services.AddSingleton(agentOptions);
builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(agentOptions));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<AgentTelemetry>();
builder.Services.AddSingleton(sp =>
{
    // Same rule as AuthService's at-rest key: required in Production, never silently weak. Elsewhere an
    // ephemeral key keeps development working, at the price that plans die with the process.
    if (string.IsNullOrWhiteSpace(agentOptions.SigningKeyBase64))
    {
        if (builder.Environment.IsProduction())
        {
            throw new InvalidOperationException(
                "Agent:SigningKeyBase64 must be set in Production (32+ random bytes, base64: openssl rand -base64 32).");
        }

        sp.GetRequiredService<ILogger<PlanSigner>>().LogWarning(
            "Agent:SigningKeyBase64 is not set: using an ephemeral key. Plans will not survive a restart or work across instances.");
        return new PlanSigner(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32), sp.GetRequiredService<TimeProvider>());
    }

    return new PlanSigner(Convert.FromBase64String(agentOptions.SigningKeyBase64), sp.GetRequiredService<TimeProvider>());
});
builder.Services.AddScoped<PlanExecutor>();
builder.Services.AddTransient<PlanLookup>();

// Inbound: the same tokens and the same JWKS as every other service. The token that authenticates a
// request here is also what BearerForwardingHandler passes on, so this is the one place the caller's
// identity is established for everything a tool then reads.
builder.Services.AddPlatformJwtAuthentication(builder.Configuration, builder.Environment);

builder.Services.AddAuthorization();
builder.Services.AddStepUpPolicy();

builder.Services.AddHealthChecks();

builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapMcp("/mcp").RequireAuthorization();
app.MapAgentEndpoints();

// As elsewhere: liveness checks nothing (a restart does not fix an unreachable dependency);
// readiness runs whatever is tagged "ready" - currently nothing, as McpServer owns no database.
app.MapHealthChecks("/health/live", new() { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new() { Predicate = c => c.Tags.Contains("ready") }).AllowAnonymous();

app.Run();

namespace McpServer
{
    public partial class Program;
}