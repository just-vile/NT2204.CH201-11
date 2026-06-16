using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Saga.Shared.Infrastructure;

public static class OpenTelemetryExtensions
{
    public static IServiceCollection AddSagaOpenTelemetry(
        this IServiceCollection services,
        IConfiguration config,
        string serviceName,
        Action<TracerProviderBuilder>? extraTracing = null,
        Action<MeterProviderBuilder>? extraMetrics = null)
    {
        var otlpEndpoint = config["OTEL_EXPORTER_OTLP_ENDPOINT"]
            ?? config["Otel:Endpoint"]
            ?? "http://localhost:4317";

        var environment = config["ASPNETCORE_ENVIRONMENT"] ?? "Development";
        var instanceId = config["HOSTNAME"] ?? Environment.MachineName;

        services.AddOpenTelemetry()
            .ConfigureResource(r => r
                .AddService(
                    serviceName: serviceName,
                    serviceNamespace: "saga",
                    serviceVersion: ThisAssemblyVersion(),
                    serviceInstanceId: instanceId)
                .AddAttributes(new KeyValuePair<string, object>[]
                {
                    new("deployment.environment", environment)
                }))
            .WithTracing(t =>
            {
                t.AddSource("MassTransit");
                t.AddSource(TelemetryConstants.SagaActivitySourceName);
                t.AddSource(serviceName);
                t.AddAspNetCoreInstrumentation(o => o.RecordException = true);
                t.AddHttpClientInstrumentation();
                t.AddEntityFrameworkCoreInstrumentation(o => o.SetDbStatementForText = true);
                t.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
                extraTracing?.Invoke(t);
            })
            .WithMetrics(m =>
            {
                m.AddMeter("MassTransit");
                m.AddMeter(TelemetryConstants.SagaMeterName);
                m.AddMeter(serviceName);
                m.AddAspNetCoreInstrumentation();
                m.AddHttpClientInstrumentation();
                m.AddRuntimeInstrumentation();
                m.AddPrometheusExporter();
                extraMetrics?.Invoke(m);
            });

        return services;
    }

    public static IApplicationBuilder UseSagaPrometheusEndpoint(this IApplicationBuilder app)
    {
        return app.UseOpenTelemetryPrometheusScrapingEndpoint();
    }

    private static string ThisAssemblyVersion()
        => typeof(OpenTelemetryExtensions).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
}
