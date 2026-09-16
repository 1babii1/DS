using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using RewardsService.Infrastructure;
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
        .AddOtlpLogging(context.Configuration, "rewards-service"));

builder.Services.AddObservability(builder.Configuration, "rewards-service");

builder.Services.AddControllers();
builder.Services.AddEnvelopeModelStateValidation();
builder.Services.AddOpenApi();

builder.Services.AddFrameworkCors(builder.Configuration);

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.MetadataAddress = builder.Configuration["Auth:MetadataAddress"];
        options.RequireHttpsMetadata = builder.Environment.IsProduction();
        options.TokenValidationParameters.ValidIssuer = builder.Configuration["Auth:Issuer"];
        options.TokenValidationParameters.ValidAudience = builder.Configuration["Auth:Audience"];
        options.TokenValidationParameters.RoleClaimType = "role";
        options.TokenValidationParameters.NameClaimType = "name";
    });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("CanEdit", policy => policy.RequireRole(RoleNames.Admin, RoleNames.Editor));

builder.Services.AddRewardsInfrastructure(builder.Configuration);
builder.Services.AddOutboxPublisher<RewardsDbContext>(builder.Configuration, "rewards.events");

builder.Services.AddDatabaseHealthCheck<RewardsDbContext>();

// Same reasoning as EmployeeService's "write" policy: CanEdit keeps out
// unauthenticated callers, not a compromised or buggy admin/editor token.
var writeRateLimit = builder.Configuration.GetValue("RateLimiting:Write:PermitLimit", 30);
var writeRateLimitWindow = builder.Configuration.GetValue("RateLimiting:Write:WindowSeconds", 60);

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

var app = builder.Build();

app.UseRequestCorrelationId();
app.UseExceptionMiddleware();

app.UseSerilogRequestLogging();

app.MapOpenApi("/openapi/v1/swagger.json");
app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1/swagger.json", "RewardsService"));

app.ConfigureCors();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllers();
app.MapDefaultHealthChecks();

app.Run();

namespace RewardsService.Web
{
    public partial class Program;
}
