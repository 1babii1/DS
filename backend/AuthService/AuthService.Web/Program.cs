using System.Threading.RateLimiting;
using AuthService.Application;
using AuthService.Application.Database;
using AuthService.Infrastructure.Postgres;
using AuthService.Web.Configuration;
using AuthService.Web.Consumers;
using Microsoft.AspNetCore.RateLimiting;
using Serilog;
using Shared.Cors;
using Shared.HealthChecks;
using Shared.Middlewares;
using Shared.Observability;
using Shared.Outbox;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.Host.UseSerilog((context, _, configuration) =>
    configuration
        .ReadFrom.Configuration(context.Configuration)
        .AddOtlpLogging(context.Configuration, "auth-service"));

builder.Services.AddObservability(builder.Configuration, "auth-service");

builder.Services.AddControllers();
builder.Services.AddRazorPages();
builder.Services.AddEnvelopeModelStateValidation();
builder.Services.AddOpenApi();

builder.Services.AddOptions<WebClientOptions>()
    .Bind(builder.Configuration.GetSection(WebClientOptions.SectionName))
    .Validate(
        options => options.IsValid(builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Docker")),
        "Enabled web client requires a strong client secret and an HTTPS frontend origin (loopback HTTP is local-only).")
    .ValidateOnStart();

builder.Services.AddFrameworkCors(builder.Configuration);

builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));

builder.Services.AddInfrastructurePostgres(builder.Configuration);

builder.Services.AddIdentityServices(builder.Environment);
builder.Services.AddOpenIddictServer(builder.Environment, builder.Configuration);
builder.Services.AddOpenIddictQuartzScheduler();

builder.Services.AddDatabaseHealthCheck<AuthDbContext>();

builder.Services.AddOptions<EmailOptions>()
    .BindConfiguration(EmailOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddScoped<IEmailSender, SmtpEmailSender>();

builder.Services.AddScoped<IOutboxWriter, OutboxWriter>();
builder.Services.AddOutboxPublisher<AuthDbContext>(builder.Configuration, "auth.events");

builder.Services.Configure<AuthConsumerOptions>(options =>
{
    options.BootstrapServers = builder.Configuration["Kafka:BootstrapServers"]
        ?? throw new InvalidOperationException("Configuration 'Kafka:BootstrapServers' is not set.");
    options.Security = KafkaSecurityOptions.FromConfiguration(builder.Configuration);
    options.Topics = builder.Configuration.GetSection("Kafka:Topics").Get<string[]>()
        ?? throw new InvalidOperationException("Configuration 'Kafka:Topics' is not set.");
    options.GroupId = builder.Configuration["Kafka:GroupId"] ?? "auth-service";
});
builder.Services.AddHostedService<EmployeeEventsConsumer>();

// Partitioned by client IP, not global: a global limiter would let one abusive IP
// lock out every other user sharing the same bucket. Five attempts a minute is
// tight enough to blunt credential stuffing without a legitimate user who mistypes
// their password twice ever noticing.
// Bound to configuration, not hardcoded, so integration tests can raise the limit
// instead of tripping it on their own traffic - TestServer's in-memory transport
// never populates RemoteIpAddress, so every test request would otherwise land in
// the same "unknown" partition and exhaust the limit after five calls regardless of
// which test made them. Defaults match what was hardcoded before.
var authRateLimit = builder.Configuration.GetValue("RateLimiting:Auth:PermitLimit", 5);
var authRateLimitWindow = builder.Configuration.GetValue("RateLimiting:Auth:WindowSeconds", 60);

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy("auth", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = authRateLimit,
            Window = TimeSpan.FromSeconds(authRateLimitWindow),
            QueueLimit = 0,
        }));
});

var app = builder.Build();

app.UseRequestCorrelationId();
app.UseExceptionMiddleware();

app.UseSerilogRequestLogging();

app.MapOpenApi("/openapi/v1/swagger.json");
app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1/swagger.json", "AuthService"));

app.ConfigureCors();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllers();
app.MapRazorPages();
app.MapDefaultHealthChecks();

await OpenIddictSeeder.SeedAsync(app.Services);
await WebClientSeeder.SeedAsync(app.Services);

app.Run();

namespace AuthService.Web
{
    public partial class Program;
}