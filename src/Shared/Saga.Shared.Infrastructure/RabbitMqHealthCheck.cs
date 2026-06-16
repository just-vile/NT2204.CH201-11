using Microsoft.Extensions.Diagnostics.HealthChecks;
using RabbitMQ.Client;

namespace Saga.Shared.Infrastructure;

/// <summary>
/// Minimal RabbitMQ readiness check: opens a short-lived connection per probe and returns Healthy
/// when the broker accepts AMQP. Avoids depending on the volatile AspNetCore.HealthChecks.Rabbitmq API.
/// </summary>
internal sealed class RabbitMqHealthCheck : IHealthCheck
{
    private readonly ConnectionFactory _factory;

    public RabbitMqHealthCheck(ConnectionFactory factory)
    {
        _factory = factory;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = _factory.CreateConnection();
            return Task.FromResult(connection.IsOpen
                ? HealthCheckResult.Healthy("rabbitmq reachable")
                : HealthCheckResult.Unhealthy("rabbitmq connection not open"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("rabbitmq unreachable", ex));
        }
    }
}