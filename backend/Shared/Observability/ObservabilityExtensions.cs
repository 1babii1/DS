using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Exceptions;
using Serilog.Sinks.OpenTelemetry;

namespace Shared.Observability;

/// <summary>
/// Shared tracing/metrics/log-correlation wiring for every service: traces and
/// metrics via OpenTelemetry, an OTLP log sink layered on top of each service's
/// existing Serilog configuration (Seq was retired once this shipped - Loki plus
/// Grafana's log-to-trace correlation replaced it, not duplicated it). Everything
/// goes to the otel-collector and from there to Tempo / Loki / Prometheus. Opt-in:
/// nothing here runs unless OTEL_EXPORTER_OTLP_ENDPOINT is actually set (see
/// docker-compose.yml's "obs" profile), so a service started without the
/// observability stack behaves exactly as it did before this existed.
/// </summary>
public static class ObservabilityExtensions
{
    /// <summary>
    /// Layers an OTLP sink onto whatever Serilog configuration the service already
    /// builds via <c>ReadFrom.Configuration</c> - call this from inside the same
    /// <c>UseSerilog</c> delegate, after <c>ReadFrom.Configuration(...)</c>, passing the
    /// same <see cref="LoggerConfiguration"/> through.
    /// </summary>
    public static LoggerConfiguration AddOtlpLogging(
        this LoggerConfiguration loggerConfiguration,
        IConfiguration configuration,
        string serviceName)
    {
        ArgumentNullException.ThrowIfNull(loggerConfiguration);
        ArgumentNullException.ThrowIfNull(configuration);

        loggerConfiguration.Enrich.WithExceptionDetails();

        string? otlpEndpoint = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        if (string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            return loggerConfiguration;
        }

        // Without an explicit service.name here, logs land in Loki as
        // "unknown_service:dotnet" and never correlate with that same service's
        // traces (the whole point of shipping both to the same collector).
        return loggerConfiguration.WriteTo.OpenTelemetry(options =>
        {
            options.Endpoint = otlpEndpoint;
            options.Protocol = OtlpProtocol.Grpc;
            options.ResourceAttributes = new Dictionary<string, object>
            {
                ["service.name"] = serviceName,
            };
        });
    }

    /// <summary>Traces and metrics. Exported via OTLP; endpoint from <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>.</summary>
    public static IServiceCollection AddObservability(
        this IServiceCollection services,
        IConfiguration configuration,
        string serviceName)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        string? otlpEndpoint = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        if (string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            return services;
        }

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(options =>
                {
                    // Health probes fire every few seconds - without this filter they
                    // flood Tempo with noise and bury the traces that actually matter.
                    options.Filter = httpContext =>
                        !httpContext.Request.Path.StartsWithSegments("/health");
                    options.RecordException = true;
                })
                .AddHttpClientInstrumentation(options => options.RecordException = true)
                .AddSource(serviceName)
                .AddOtlpExporter())
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(serviceName)
                .AddOtlpExporter());

        return services;
    }
}
