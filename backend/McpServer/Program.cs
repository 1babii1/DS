using McpServer.Embeddings;
using McpServer.HealthChecks;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Microsoft.Extensions.Options;
using Npgsql;
using Shared.Observability;
using Shared.Security;

Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddObservability(builder.Configuration, "mcp-server");

builder.Services.AddOptions<EmbeddingsOptions>()
    .Bind(builder.Configuration.GetSection(EmbeddingsOptions.SectionName));

builder.Services.AddSingleton(_ =>
{
    var dataSourceBuilder = new NpgsqlDataSourceBuilder(builder.Configuration.GetConnectionString("PlatformDb"));
    dataSourceBuilder.UseVector();
    return dataSourceBuilder.Build();
});

builder.Services.AddHttpClient<OllamaEmbeddingClient>((sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<EmbeddingsOptions>>().Value;
    client.BaseAddress = new Uri(options.OllamaBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
})

    // Same shape as DirectoryService's own Ollama client (see its Program.cs) - a brief
    // restart of the embedding model becomes a retried call instead of a failed tool
    // invocation, and the breaker stops every semantic-search tool call from separately
    // paying a 30s timeout once Ollama is confirmed down.
    .AddResilienceHandler("ollama-embeddings", builder =>
    {
        builder.AddRetry(new()
        {
            MaxRetryAttempts = 2,
            Delay = TimeSpan.FromMilliseconds(500),
            BackoffType = DelayBackoffType.Exponential,
        });

        builder.AddCircuitBreaker(new()
        {
            FailureRatio = 0.5,
            SamplingDuration = TimeSpan.FromSeconds(30),
            MinimumThroughput = 5,
            BreakDuration = TimeSpan.FromSeconds(15),
        });
    });

// Инструменты отдают данные сразу нескольких сервисов, включая ФИО и email
// сотрудников, в обход правил доступа, которые эти сервисы проверяют у себя.
// Поэтому те же токены и тот же JWKS, что и везде: без аутентификации этот
// endpoint был самым коротким путём к персональным данным во всей системе.
builder.Services.AddPlatformJwtAuthentication(builder.Configuration, builder.Environment);

builder.Services.AddAuthorization();

builder.Services
    .AddHealthChecks()
    .AddCheck<NpgsqlDataSourceHealthCheck>("database", tags: ["ready"]);

builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapMcp("/mcp").RequireAuthorization();

// Как и в остальных сервисах: liveness ничего не проверяет (перезапуск не
// чинит недоступную базу), readiness проверяет то, что помечено тегом "ready".
app.MapHealthChecks("/health/live", new() { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new() { Predicate = c => c.Tags.Contains("ready") }).AllowAnonymous();

app.Run();

namespace McpServer
{
    public partial class Program;
}