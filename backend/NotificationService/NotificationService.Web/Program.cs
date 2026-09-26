using Shared.Ops;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Hosting;
using NotificationService.Infrastructure.Postgres;
using NotificationService.Web;
using NotificationService.Web.Consumers;
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
        .AddOtlpLogging(context.Configuration, "notification-service"));

builder.Services.AddObservability(builder.Configuration, "notification-service");

builder.Services.AddControllers();
builder.Services.AddEnvelopeModelStateValidation();
builder.Services.AddOpenApi();

// Redis backplane: without it, NotificationsHub's connection groups live only in this
// process's memory, so a user connected to one instance never gets a push whose event was
// processed on another - invisible today (single instance everywhere, see docker-compose.yml),
// real the moment this service is ever scaled out. Shares the same Redis DirectoryService's
// HybridCache already uses; ChannelPrefix keeps the two services' pub/sub channels distinct on
// that one shared instance.
var redisConnectionString = builder.Configuration.GetConnectionString("Redis")
    ?? throw new InvalidOperationException("Connection string 'Redis' is not configured.");
builder.Services.AddSignalR()
    .AddStackExchangeRedis(redisConnectionString, options =>
        options.Configuration.ChannelPrefix = StackExchange.Redis.RedisChannel.Literal("notification-service"));

builder.Services.AddFrameworkCors(builder.Configuration);

builder.Services.AddPlatformJwtAuthentication(
    builder.Configuration,
    builder.Environment,
    options =>
    {
        // SignalR's browser transport can't set an Authorization header on the WebSocket
        // upgrade handshake itself, so the JS client instead passes the token as a query
        // string parameter - a standard, documented ASP.NET Core SignalR pattern, not a
        // workaround. Scoped to the hub path only: REST calls still authenticate via the
        // normal Authorization header.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;

                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hub"))
                {
                    context.Token = accessToken;
                }

                return Task.CompletedTask;
            },
        };
    });

builder.Services.AddAuthorization();

builder.Services.AddNotificationInfrastructure(builder.Configuration);

builder.Services.Configure<DomainEventsConsumerOptions>(options =>
{
    options.BootstrapServers = builder.Configuration["Kafka:BootstrapServers"]
        ?? throw new InvalidOperationException("Configuration 'Kafka:BootstrapServers' is not set.");
    options.Security = KafkaSecurityOptions.FromConfiguration(builder.Configuration);
    options.Topics = builder.Configuration.GetSection("Kafka:Topics").Get<string[]>()
        ?? throw new InvalidOperationException("Configuration 'Kafka:Topics' is not set.");
    options.GroupId = builder.Configuration["Kafka:GroupId"] ?? "notification-service";
});
builder.Services.AddHostedService<DomainEventsConsumer>();

builder.Services.AddDatabaseHealthCheck<NotificationDbContext>();
builder.Services.AddKafkaHealthCheck(
    builder.Configuration["Kafka:BootstrapServers"]
        ?? throw new InvalidOperationException("Configuration 'Kafka:BootstrapServers' is not set."),
    KafkaSecurityOptions.FromConfiguration(builder.Configuration));

builder.Services.AddOpsPolicy();
builder.Services.AddOpsMetrics<NotificationDbContext>("notification-service");

var app = builder.Build();

app.UseRequestCorrelationId();
app.UseExceptionMiddleware();

app.UseSerilogRequestLogging();

app.MapOpenApi("/openapi/v1/swagger.json");
app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1/swagger.json", "NotificationService"));

app.ConfigureCors();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapDeadLetterOps<NotificationDbContext>("/api/notifications/ops");
app.MapHub<NotificationsHub>("/hub/notifications");
app.MapDefaultHealthChecks();

app.Run();

namespace NotificationService.Web
{
    public partial class Program;
}
