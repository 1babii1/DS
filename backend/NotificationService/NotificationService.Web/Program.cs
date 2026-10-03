using Shared.Resilience;
using Shared.Avro;
using Shared.Ops;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using NotificationService.Infrastructure.Postgres;
using NotificationService.Web;
using NotificationService.Web.Consumers;
using NotificationService.Web.HubTickets;
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
    .AddStackExchangeRedis(Shared.Redis.RedisConnectionString.Resilient(redisConnectionString), options =>
        options.Configuration.ChannelPrefix = StackExchange.Redis.RedisChannel.Literal("notification-service"));

builder.Services.AddFrameworkCors(builder.Configuration);

// REST endpoints authenticate with the OAuth bearer token from the Authorization header, and only that. The hub has
// its own scheme (HubTicketAuthenticationHandler): a browser WebSocket cannot set a header, and the browser must not
// hold the OAuth token, so the hub accepts a short-lived ticket from the access_token query parameter instead. Neither
// credential works on the other's endpoints (ADR 0022).
builder.Services.AddPlatformJwtAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddAuthentication()
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, HubTicketAuthenticationHandler>(
        HubTicketDefaults.Scheme, _ => { });

builder.Services.AddOptions<HubTicketOptions>().Bind(builder.Configuration.GetSection(HubTicketOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp =>
{
    // Same rule as the agent's plan key and AuthService's at-rest key: required in Production, never silently weak.
    // Elsewhere an ephemeral key keeps development working, at the price that tickets do not survive a restart or
    // work across instances (they only live a minute, so a restart costs a reconnect).
    var options = sp.GetRequiredService<IOptions<HubTicketOptions>>().Value;
    if (string.IsNullOrWhiteSpace(options.SigningKeyBase64))
    {
        if (builder.Environment.IsProduction())
        {
            throw new InvalidOperationException(
                "HubTickets:SigningKeyBase64 must be set in Production (32+ random bytes, base64: openssl rand -base64 32).");
        }

        sp.GetRequiredService<ILogger<HubTicketService>>().LogWarning(
            "HubTickets:SigningKeyBase64 is not set: using an ephemeral key. Hub tickets will not work across instances.");
        options = new HubTicketOptions { SigningKeyBase64 = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)) };
    }

    return new HubTicketService(Options.Create(options), sp.GetRequiredService<TimeProvider>());
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
builder.Services.AddEventAvroDecoder(builder.Configuration, typeof(NotificationService.Web.Consumers.DomainEventsConsumer).Assembly);
builder.Services.AddHostedService<DomainEventsConsumer>();

builder.Services.AddDatabaseHealthCheck<NotificationDbContext>();
builder.Services.AddKafkaHealthCheck(
    builder.Configuration["Kafka:BootstrapServers"]
        ?? throw new InvalidOperationException("Configuration 'Kafka:BootstrapServers' is not set."),
    KafkaSecurityOptions.FromConfiguration(builder.Configuration));

builder.Services.AddOpsPolicy();
builder.Services.AddOpsMetrics<NotificationDbContext>("notification-service");

// Turn requests away quickly past capacity instead of answering all of them slowly (ADR 0038).
builder.Services.AddLoadShedding(builder.Configuration);

var app = builder.Build();

app.UseRequestCorrelationId();
app.UseExceptionMiddleware();
app.UseLoadShedding();

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
