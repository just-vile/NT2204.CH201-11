using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;

namespace Saga.Shared.Infrastructure;

public static class HostingExtensions
{
    /// <summary>One-call setup: Serilog + correlation logging defaults.</summary>
    public static WebApplicationBuilder UseSagaSerilog(this WebApplicationBuilder builder, string serviceName)
    {
        builder.Host.UseSerilog((ctx, lc) => lc
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithEnvironmentName()
            .Enrich.WithProperty("service", serviceName)
            .WriteTo.Console(formatter: new Serilog.Formatting.Json.JsonFormatter())
            .ReadFrom.Configuration(ctx.Configuration));
        return builder;
    }

    /// <summary>
    /// Configures System.Text.Json defaults shared across services:
    ///   - enums serialized as strings (`"Pending"` not `0`),
    ///   - ignore null values,
    ///   - case-insensitive deserialization.
    /// </summary>
    public static IServiceCollection AddSagaJsonDefaults(this IServiceCollection services)
    {
        services.Configure<JsonOptions>(o => ApplyDefaults(o.SerializerOptions));
        services.Configure<Microsoft.AspNetCore.Mvc.JsonOptions>(o => ApplyDefaults(o.JsonSerializerOptions));
        return services;

        static void ApplyDefaults(JsonSerializerOptions opt)
        {
            opt.PropertyNameCaseInsensitive = true;
            opt.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            if (!opt.Converters.OfType<JsonStringEnumConverter>().Any())
            {
                opt.Converters.Add(new JsonStringEnumConverter());
            }
        }
    }

    /// <summary>
    /// Adds standard liveness + readiness checks. Liveness is process-only (no deps),
    /// readiness requires Postgres + RabbitMQ to be reachable.
    /// </summary>
    public static IServiceCollection AddSagaHealthChecks(
        this IServiceCollection services,
        IConfiguration config,
        string postgresConnectionStringName = "Postgres")
    {
        var postgres = config.GetConnectionString(postgresConnectionStringName)
            ?? throw new InvalidOperationException($"ConnectionStrings:{postgresConnectionStringName} missing");

        var rabbitHost = config["RabbitMq:Host"] ?? "localhost";
        var rabbitUser = config["RabbitMq:Username"] ?? "guest";
        var rabbitPass = config["RabbitMq:Password"] ?? "guest";
        var rabbitVHost = config["RabbitMq:VirtualHost"] ?? "/";

        services.AddSingleton(_ => new RabbitMQ.Client.ConnectionFactory
        {
            HostName = rabbitHost,
            UserName = rabbitUser,
            Password = rabbitPass,
            VirtualHost = rabbitVHost,
            RequestedConnectionTimeout = TimeSpan.FromSeconds(3)
        });
        services.AddSingleton<RabbitMqHealthCheck>();

        services.AddHealthChecks()
            .AddNpgSql(postgres, name: "postgres", tags: new[] { "ready" })
            .AddCheck<RabbitMqHealthCheck>("rabbitmq", tags: new[] { "ready" });

        return services;
    }

    /// <summary>Liveness + readiness endpoints under /healthz/live and /healthz/ready.</summary>
    public static IEndpointRouteBuilder MapSagaHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/healthz/live", new HealthCheckOptions
        {
            Predicate = _ => false, // process-only
            ResponseWriter = WriteStatus
        });

        endpoints.MapHealthChecks("/healthz/ready", new HealthCheckOptions
        {
            Predicate = c => c.Tags.Contains("ready"),
            ResponseWriter = WriteStatus
        });

        return endpoints;
    }

    private static Task WriteStatus(HttpContext ctx, HealthReport report)
    {
        ctx.Response.ContentType = "application/json";
        var payload = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                error = e.Value.Exception?.Message
            })
        };
        return ctx.Response.WriteAsync(JsonSerializer.Serialize(payload));
    }
}
