using Shared.Ops;
using System.Threading.RateLimiting;
using EmployeeService.Application.Database;
using EmployeeService.Application.Employees.Commands;
using EmployeeService.Application.Employees.Queries;
using EmployeeService.Infrastructure.DirectoryGrpc;
using EmployeeService.Infrastructure.Postgres;
using EmployeeService.Web.Consumers;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Serilog;
using Shared.Cors;
using Shared.HealthChecks;
using Shared.Middlewares;
using Shared.Observability;
using Shared.Outbox;
using Shared.Security;

// Required for the gRPC client to call DirectoryService over cleartext HTTP/2 (h2c) -
// no TLS between services inside the docker network for this pet project.
AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.Host.UseSerilog((context, _, configuration) =>
    configuration
        .ReadFrom.Configuration(context.Configuration)
        .AddOtlpLogging(context.Configuration, "employee-service"));

builder.Services.AddObservability(builder.Configuration, "employee-service");

builder.Services.AddControllers();
builder.Services.AddEnvelopeModelStateValidation();
builder.Services.AddOpenApi();

builder.Services.AddFrameworkCors(builder.Configuration);

builder.Services.AddPlatformJwtAuthentication(builder.Configuration, builder.Environment);

builder.Services.AddCanEditPolicy();
builder.Services.AddStepUpPolicy();

builder.Services.AddInfrastructurePostgres(builder.Configuration);
builder.Services.AddDirectoryGrpcClient(builder.Configuration);

builder.Services.AddScoped<IOutboxWriter, OutboxWriter>();
builder.Services.AddOutboxPublisher<EmployeeDbContext>(builder.Configuration, "employee.events");

builder.Services.Configure<EmployeeConsumerOptions>(options =>
{
    options.BootstrapServers = builder.Configuration["Kafka:BootstrapServers"]
        ?? throw new InvalidOperationException("Configuration 'Kafka:BootstrapServers' is not set.");
    options.Security = KafkaSecurityOptions.FromConfiguration(builder.Configuration);
    options.Topics = builder.Configuration.GetSection("Kafka:Topics").Get<string[]>()
        ?? throw new InvalidOperationException("Configuration 'Kafka:Topics' is not set.");
    options.GroupId = builder.Configuration["Kafka:GroupId"] ?? "employee-service";
});
builder.Services.AddHostedService<AuthEventsConsumer>();

builder.Services.AddScoped<HireEmployeeHandler>();
builder.Services.AddScoped<TransferEmployeeHandler>();
builder.Services.AddScoped<TerminateEmployeeHandler>();
builder.Services.AddScoped<GetEmployeeByIdHandler>();
builder.Services.AddScoped<ListEmployeesHandler>();

builder.Services.AddDatabaseHealthCheck<EmployeeDbContext>();
builder.Services.AddKafkaHealthCheck(
    builder.Configuration["Kafka:BootstrapServers"]
        ?? throw new InvalidOperationException("Configuration 'Kafka:BootstrapServers' is not set."),
    KafkaSecurityOptions.FromConfiguration(builder.Configuration));

// Hire/Transfer/Terminate were unthrottled - CanEdit keeps out unauthenticated
// callers, but not a compromised or buggy admin/editor token. Same
// IP-partitioned fixed window as DirectoryService's "write" policy, config-driven
// for the same reason: TestServer never populates RemoteIpAddress.
var writeRateLimit = builder.Configuration.GetValue("RateLimiting:Write:PermitLimit", 30);
var writeRateLimitWindow = builder.Configuration.GetValue("RateLimiting:Write:WindowSeconds", 60);

// Must be configured for the rate limiter below to see the real client IP
// instead of nginx's - see ForwardedHeadersExtensions for why that matters.
builder.Services.AddProxyForwardedHeaders(builder.Configuration);

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

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

var app = builder.Build();

app.UseProxyForwardedHeaders();

app.UseRequestCorrelationId();
app.UseExceptionMiddleware();

app.UseSerilogRequestLogging();

app.MapOpenApi("/openapi/v1/swagger.json");
app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1/swagger.json", "EmployeeService"));

app.ConfigureCors();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllers();
app.MapOutboxOps<EmployeeDbContext>("/api/employees/ops");
app.MapDeadLetterOps<EmployeeDbContext>("/api/employees/ops");
app.MapDefaultHealthChecks();

app.Run();

namespace EmployeeService.Web
{
    public partial class Program;
}