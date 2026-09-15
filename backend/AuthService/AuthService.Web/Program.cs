using System.Threading.RateLimiting;
using AuthService.Application;
using AuthService.Infrastructure.Postgres;
using AuthService.Web.Configuration;
using Microsoft.AspNetCore.RateLimiting;
using Serilog;
using Shared.Cors;
using Shared.HealthChecks;
using Shared.Middlewares;
using Shared.Observability;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.Host.UseSerilog((context, _, configuration) =>
    configuration
        .ReadFrom.Configuration(context.Configuration)
        .AddOtlpLogging(context.Configuration, "auth-service"));

builder.Services.AddObservability(builder.Configuration, "auth-service");

builder.Services.AddControllers();
builder.Services.AddEnvelopeModelStateValidation();
builder.Services.AddOpenApi();

builder.Services.AddFrameworkCors(builder.Configuration);

builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));

builder.Services.AddInfrastructurePostgres(builder.Configuration);

builder.Services.AddIdentityServices();
builder.Services.AddOpenIddictServer(builder.Environment, builder.Configuration);

builder.Services.AddDatabaseHealthCheck<AuthDbContext>();

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
app.MapDefaultHealthChecks();

await OpenIddictSeeder.SeedAsync(app.Services);

app.Run();

namespace AuthService.Web
{
    public partial class Program;
}