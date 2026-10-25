using System.Diagnostics;
using Contrapost.Application.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Contrapost.Infrastructure.Observability;

/// <summary>
/// OpenTelemetry for every Contrapost process (design doc §12). Traces and metrics are exported over
/// OTLP when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set (to the collector in Compose); without it,
/// spans and measurements are still produced, so tests can observe them, but nothing is sent.
/// </summary>
public static class Telemetry
{
    public const string SourceName = "Contrapost";

    /// <summary>Spans for work the libraries don't trace themselves: relaying and consuming events.</summary>
    public static readonly ActivitySource Source = new(SourceName);

    public static IServiceCollection AddTelemetry(
        this IServiceCollection services,
        IConfiguration configuration,
        string serviceName,
        Action<TracerProviderBuilder>? tracing = null,
        Action<MeterProviderBuilder>? metrics = null)
    {
        var export = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] is { Length: > 0 };

        services.AddMetrics();
        services.AddSingleton<LedgerMetrics>();
        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(builder =>
            {
                builder.AddSource(SourceName).AddNpgsql();
                tracing?.Invoke(builder);
                if (export)
                {
                    builder.AddOtlpExporter();
                }
            })
            .WithMetrics(builder =>
            {
                builder.AddMeter(LedgerMetrics.MeterName, "Npgsql").AddRuntimeInstrumentation();
                metrics?.Invoke(builder);
                if (export)
                {
                    builder.AddOtlpExporter();
                }
            });

        return services;
    }

    /// <summary>The W3C traceparent of the current span, if there is one, for storing alongside an outbox row.</summary>
    public static string? CurrentTraceparent() =>
        Activity.Current is { IdFormat: ActivityIdFormat.W3C } activity ? activity.Id : null;

    /// <summary>Starts a span that continues the trace in <paramref name="traceparent"/>, or a new trace if it is missing.</summary>
    public static Activity? StartLinked(string name, ActivityKind kind, string? traceparent) =>
        Source.StartActivity(name, kind, ActivityContext.TryParse(traceparent, null, out var parent) ? parent : default);
}
