using Microsoft.Extensions.Hosting;
using NotificationService;
using Serilog;
using Shared.HealthChecks;
using Shared.Observability;

var builder = WebApplication.CreateBuilder(args);

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .AddOtlpLogging(builder.Configuration, "notification-service")
    .CreateLogger();

builder.Services.AddSerilog();
builder.Services.AddObservability(builder.Configuration, "notification-service");

// A BackgroundService exception is swallowed by default and the process keeps running
// with a dead consumer - nothing downstream would ever know. StopHost makes a fatal
// consumer failure visible the same way every other dependency failure is: the health
// check (and with it, docker's restart policy) reacts instead of a silent no-op worker.
builder.Services.Configure<HostOptions>(options =>
    options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost);

builder.Services.Configure<NotificationOptions>(options =>
{
    options.BootstrapServers = builder.Configuration["Kafka:BootstrapServers"]
        ?? throw new InvalidOperationException("Configuration 'Kafka:BootstrapServers' is not set.");
    options.Topics = builder.Configuration.GetSection("Kafka:Topics").Get<string[]>()
        ?? throw new InvalidOperationException("Configuration 'Kafka:Topics' is not set.");
    options.GroupId = builder.Configuration["Kafka:GroupId"] ?? "notification-service";
});

builder.Services.AddHostedService<NotificationWorker>();
builder.Services.AddHealthChecks();

var app = builder.Build();

app.MapDefaultHealthChecks();

app.Run();
