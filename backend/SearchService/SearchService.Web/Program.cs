using Shared.Ops;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using SearchService.Infrastructure.Elasticsearch;
using Microsoft.Extensions.Options;
using SearchService.Infrastructure.Postgres;
using SearchService.Infrastructure.Postgres.Embeddings;
using SearchService.Web.Consumers;
using Serilog;
using Shared.Cors;
using Shared.HealthChecks;
using Shared.Middlewares;
using Shared.Observability;
using Shared.Outbox;
using Shared.Security;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.Host.UseSerilog((context, _, configuration) =>
    configuration
        .ReadFrom.Configuration(context.Configuration)
        .AddOtlpLogging(context.Configuration, "search-service"));

builder.Services.AddObservability(builder.Configuration, "search-service");

builder.Services.AddControllers();
builder.Services.AddEnvelopeModelStateValidation();
builder.Services.AddOpenApi();

builder.Services.AddFrameworkCors(builder.Configuration);

builder.Services.AddPlatformJwtAuthentication(builder.Configuration, builder.Environment);

builder.Services.AddAuthorization();

builder.Services.AddSearchPostgresInfrastructure(builder.Configuration);
builder.Services.AddSearchElasticsearchInfrastructure(builder.Configuration);

// The semantic side: vectors are made in the background from the text the consumer stages, by the same model
// DirectoryService uses. A missing model only delays semantic results; keyword search and consumption do not depend on it.
builder.Services.AddOptions<EmbeddingsOptions>().Bind(builder.Configuration.GetSection(EmbeddingsOptions.SectionName));
builder.Services.AddHttpClient<IEmbeddingClient, OllamaEmbeddingClient>((sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<EmbeddingsOptions>>().Value;
    client.BaseAddress = new Uri(options.OllamaBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddHostedService<DocumentEmbeddingWorker>();

builder.Services.Configure<DomainEventsConsumerOptions>(options =>
{
    options.BootstrapServers = builder.Configuration["Kafka:BootstrapServers"]
        ?? throw new InvalidOperationException("Configuration 'Kafka:BootstrapServers' is not set.");
    options.Security = KafkaSecurityOptions.FromConfiguration(builder.Configuration);
    options.Topics = builder.Configuration.GetSection("Kafka:Topics").Get<string[]>()
        ?? throw new InvalidOperationException("Configuration 'Kafka:Topics' is not set.");
    options.GroupId = builder.Configuration["Kafka:GroupId"] ?? "search-service";
});
builder.Services.AddHostedService<DomainEventsConsumer>();

builder.Services.AddDatabaseHealthCheck<SearchDbContext>();
builder.Services.AddKafkaHealthCheck(
    builder.Configuration["Kafka:BootstrapServers"]
        ?? throw new InvalidOperationException("Configuration 'Kafka:BootstrapServers' is not set."),
    KafkaSecurityOptions.FromConfiguration(builder.Configuration));

builder.Services.AddOpsPolicy();
builder.Services.AddOpsMetrics<SearchDbContext>("search-service");

var app = builder.Build();

app.UseRequestCorrelationId();
app.UseExceptionMiddleware();

app.UseSerilogRequestLogging();

app.MapOpenApi("/openapi/v1/swagger.json");
app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1/swagger.json", "SearchService"));

app.ConfigureCors();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapDeadLetterOps<SearchDbContext>("/api/search/ops");
app.MapDefaultHealthChecks();

app.Run();

namespace SearchService.Web
{
    public partial class Program;
}
