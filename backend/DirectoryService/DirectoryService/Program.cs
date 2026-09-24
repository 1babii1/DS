using Shared.Ops;
using System.Threading.RateLimiting;
using DirectoryService.Application.Database;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using DirectoryService.Application.Department.Commands;
using DirectoryService.Application.Department.Queries;
using DirectoryService.Application.Location.Commands;
using DirectoryService.Application.Location.Queries;
using DirectoryService.Application.Position;
using DirectoryService.Application.Position.Queries;
using DirectoryService.Application.Search;
using DirectoryService.Grpc;
using DirectoryService.Infrastructure.Postgres;
using DirectoryService.Infrastructure.Postgres.Backgrounds;
using DirectoryService.Infrastructure.Postgres.Database;
using DirectoryService.Infrastructure.Postgres.Embeddings;
using DirectoryService.Infrastructure.Postgres.Repositories.Departments;
using DirectoryService.Infrastructure.Postgres.Repositories.Locations;
using DirectoryService.Infrastructure.Postgres.Repositories.Positions;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;
using Serilog;
using Shared;
using Shared.Cors;
using Shared.HealthChecks;
using Shared.Middlewares;
using Shared.Observability;
using Shared.Outbox;
using Shared.Security;

var builder = WebApplication.CreateBuilder(args);

// Cleartext HTTP/1.1+HTTP/2 multiplexing on one Kestrel endpoint (no TLS, no ALPN) is
// unreliable in practice - the server can reject h2c requests with HTTP_1_1_REQUIRED.
// Separate ports instead: REST (via nginx) stays HTTP/1.1, gRPC (internal only, called
// directly by other services like EmployeeService) gets its own HTTP/2-only endpoint.
// Explicit Listen calls make Kestrel ignore ASPNETCORE_URLS entirely, so both ports are
// controlled here.
var restPort = builder.Configuration.GetValue("Kestrel:RestPort", 5129);
var grpcPort = builder.Configuration.GetValue("Kestrel:GrpcPort", 5179);
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(restPort, listenOptions => listenOptions.Protocols = HttpProtocols.Http1);
    options.ListenAnyIP(grpcPort, listenOptions => listenOptions.Protocols = HttpProtocols.Http2);
});

builder.Logging.ClearProviders();

builder.Logging.AddConsole();

builder.Logging.SetMinimumLevel(LogLevel.Debug);

builder.Host.UseSerilog((context, _, configuration) =>
    configuration
        .ReadFrom.Configuration(context.Configuration)
        .AddOtlpLogging(context.Configuration, "directory-service"));

builder.Services.AddObservability(builder.Configuration, "directory-service");

builder.Services.AddOpenApi();

builder.Services.AddFrameworkCors(builder.Configuration);

builder.Services.AddControllers();
builder.Services.AddEnvelopeModelStateValidation();

builder.Services.AddGrpc();

builder.Services.AddHttpLogging();

builder.Services.AddPlatformJwtAuthentication(builder.Configuration, builder.Environment);

builder.Services.AddCanEditPolicy();

builder.Services.AddValidatorsFromAssemblyContaining<CreateDepartmentValidation>();

builder.Services.AddSingleton<IConfigureOptions<JsonOptions>, InjectJSONSerializeConfig>();

builder.Services.AddScoped<DirectoryServiceDbContext>(sp =>
    new DirectoryServiceDbContext(
        builder.Configuration.GetConnectionString("DirectoryServiceDb")!,
        sp.GetRequiredService<ILoggerFactory>()));

builder.Services.AddScoped<IReadDbContext, DirectoryServiceDbContext>(sp =>
    new DirectoryServiceDbContext(
        builder.Configuration.GetConnectionString("DirectoryServiceDb")!,
        sp.GetRequiredService<ILoggerFactory>()));

builder.Services.Configure<ClearDbOptions>(builder.Configuration.GetSection("ClearDbOptions"));

builder.Services.AddHostedService<ClearDbOfDeletedEntities>();

builder.Services.AddSingleton<IDbConnectionFactory, NpgsqlConnectionFactory>();
Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;

builder.Services.AddScoped<ITransactionManager, TransactionManager>();

builder.Services.AddScoped<IOutboxWriter, OutboxWriter>();
builder.Services.AddOutboxPublisher<DirectoryServiceDbContext>(builder.Configuration, "directory.events");

builder.Services.AddKafkaHealthCheck(
    builder.Configuration["Kafka:BootstrapServers"]
        ?? throw new InvalidOperationException("Configuration 'Kafka:BootstrapServers' is not set."),
    KafkaSecurityOptions.FromConfiguration(builder.Configuration));

builder.Services.AddOptions<EmbeddingsOptions>()
    .Bind(builder.Configuration.GetSection(EmbeddingsOptions.SectionName));
builder.Services.AddHttpClient<IEmbeddingClient, OllamaEmbeddingClient>((sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<EmbeddingsOptions>>().Value;
    client.BaseAddress = new Uri(options.OllamaBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
})

    // DepartmentEmbeddingWorker's own catch already treats any failure here (including a
    // tripped breaker) as "retry on the next poll pass" - so adding retry/circuit-breaking
    // only makes a brief Ollama restart transparent instead of skipping straight to the
    // next poll interval, without changing what happens when it stays down.
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
builder.Services.AddHostedService<DepartmentEmbeddingWorker>();
builder.Services.AddScoped<IDepartmentSemanticSearch, DepartmentSemanticSearchService>();
builder.Services.AddScoped<SearchDepartmentsSemanticHandler>();

builder.Services.AddScoped<ILocationsRepository, EfCoreLocationsRepository>();

builder.Services.AddScoped<IPositionRepository, EfCorePositionRepository>();

builder.Services.AddScoped<IDepartmentRepository, EfCoreDepartmentsRepository>();

builder.Services.AddScoped<CreateLocationHandle>();

builder.Services.AddScoped<CreatePositionHandle>();

builder.Services.AddScoped<CreateDepartmentHandler>();

builder.Services.AddScoped<UpdateDepartmentLocationsHandler>();

builder.Services.AddScoped<UpdateParentDepartmentHandler>();

builder.Services.AddScoped<GetLocationByIdHandle>();

builder.Services.AddScoped<GetLocationByDepartmentHandle>();

builder.Services.AddScoped<GetLocationsHandler>();

builder.Services.AddScoped<GetPositionsHandler>();

builder.Services.AddScoped<GetDepartmentByIdHandler>();

builder.Services.AddScoped<GetDepartmentByLocationHandler>();

builder.Services.AddScoped<GetDepartmentsTopByPositionsHandler>();

builder.Services.AddScoped<GetParentDepartmentsHandler>();

builder.Services.AddScoped<GetChildrenLazyHandler>();

builder.Services.AddScoped<SoftDeleteDepartmentHandler>();

builder.Services.AddStackExchangeRedisCache(setup =>
{
    setup.Configuration = builder.Configuration.GetConnectionString("Redis");
});

builder.Services.AddHybridCache(options => options.DefaultEntryOptions = new HybridCacheEntryOptions
{
    LocalCacheExpiration = TimeSpan.FromMinutes(5),
    Expiration = TimeSpan.FromMinutes(30),
});

builder.Services.AddDatabaseHealthCheck<DirectoryServiceDbContext>();

// Semantic search does an Ollama round trip plus a vector-distance query on every
// call - an authenticated caller in a tight loop (a buggy client, a compromised
// token) can still drive real load per request, unlike a plain indexed read.
// 30/min per IP is generous for real usage, tight enough to blunt a loop.
// Bound to configuration, not hardcoded - same reasoning as AuthService's "auth"
// policy: a future test suite that exercises /api/departments/search through real
// HTTP needs to be able to raise this, since TestServer never populates
// RemoteIpAddress and every call would otherwise share one partition.
var searchRateLimit = builder.Configuration.GetValue("RateLimiting:Search:PermitLimit", 30);
var searchRateLimitWindow = builder.Configuration.GetValue("RateLimiting:Search:WindowSeconds", 60);

// Every Create/Update/Delete on Department/Location/Position was previously
// unthrottled - CanEdit already keeps out unauthenticated callers, but a
// compromised or simply buggy admin/editor token could still hammer writes with
// nothing to blunt it. Same IP-partitioned fixed window as "search" and
// AuthService's "auth" policy, config-driven for the same reason: TestServer
// never populates RemoteIpAddress, so a real test suite needs to raise this.
var writeRateLimit = builder.Configuration.GetValue("RateLimiting:Write:PermitLimit", 30);
var writeRateLimitWindow = builder.Configuration.GetValue("RateLimiting:Write:WindowSeconds", 60);

// Must be configured for the rate limiter below to see the real client IP
// instead of nginx's - see ForwardedHeadersExtensions for why that matters.
builder.Services.AddProxyForwardedHeaders(builder.Configuration);

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy("search", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = searchRateLimit,
            Window = TimeSpan.FromSeconds(searchRateLimitWindow),
            QueueLimit = 0,
        }));

    options.AddPolicy("write", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = writeRateLimit,
            Window = TimeSpan.FromSeconds(writeRateLimitWindow),
            QueueLimit = 0,
        }));
});

builder.Services.AddOpsPolicy();
builder.Services.AddStepUpPolicy();

var app = builder.Build();

app.UseProxyForwardedHeaders();

app.UseRequestCorrelationId();
app.UseExceptionMiddleware();

app.UseSerilogRequestLogging();

app.UseHttpLogging();

// Configure the HTTP request pipeline.
// if (app.Environment.IsDevelopment() || app.Environment.Is)
app.MapOpenApi("/openapi/v1/swagger.json");

app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1/swagger.json", "DirectoryService"));

app.ConfigureCors();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllers();
app.MapOutboxOps<DirectoryServiceDbContext>("/api/ops/directory");
app.MapDefaultHealthChecks();
app.MapGrpcService<DirectoryLookupService>();

app.Run();

namespace DirectoryService
{
    public partial class Program;
}