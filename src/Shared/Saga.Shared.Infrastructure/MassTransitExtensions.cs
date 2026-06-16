using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Saga.Shared.Infrastructure;

public static class MassTransitExtensions
{
    /// <summary>
    /// Wires MassTransit + RabbitMQ + EF Core transactional outbox/inbox for a service.
    /// <paramref name="serviceName"/> is used as the queue-name prefix so two services that
    /// happen to name their consumers the same (e.g. PaymentFailedConsumer in OrderService and
    /// InventoryService) get distinct queues and both receive every published event.
    /// </summary>
    public static IServiceCollection AddSagaMassTransit<TDbContext>(
        this IServiceCollection services,
        IConfiguration config,
        string serviceName,
        Action<IBusRegistrationConfigurator> configureConsumers,
        Action<IRabbitMqBusFactoryConfigurator, IBusRegistrationContext>? configureEndpoints = null)
        where TDbContext : DbContext
    {
        services.AddMassTransit(x =>
        {
            x.SetEndpointNameFormatter(new KebabCaseEndpointNameFormatter(prefix: serviceName, includeNamespace: false));

            // Transactional outbox + inbox (idempotent consume) backed by the service DbContext.
            x.AddEntityFrameworkOutbox<TDbContext>(o =>
            {
                o.UsePostgres();
                o.UseBusOutbox();
                o.QueryDelay = TimeSpan.FromSeconds(1);
                o.DuplicateDetectionWindow = TimeSpan.FromMinutes(30);
            });

            configureConsumers(x);

            x.UsingRabbitMq((ctx, cfg) =>
            {
                // Resolve broker settings from the DI-built IConfiguration so test-time
                // overrides applied via WebApplicationFactory ConfigureAppConfiguration
                // are honored (they arrive after AddSagaMassTransit has been called).
                var liveConfig = ctx.GetRequiredService<IConfiguration>();
                var rabbitHost = liveConfig["RabbitMq:Host"] ?? "localhost";
                var rabbitPort = liveConfig.GetValue<ushort?>("RabbitMq:Port");
                var rabbitUser = liveConfig["RabbitMq:Username"] ?? "guest";
                var rabbitPass = liveConfig["RabbitMq:Password"] ?? "guest";
                var rabbitVHost = liveConfig["RabbitMq:VirtualHost"] ?? "/";

                if (rabbitPort.HasValue)
                {
                    cfg.Host(rabbitHost, rabbitPort.Value, rabbitVHost, h =>
                    {
                        h.Username(rabbitUser);
                        h.Password(rabbitPass);
                    });
                }
                else
                {
                    cfg.Host(rabbitHost, rabbitVHost, h =>
                    {
                        h.Username(rabbitUser);
                        h.Password(rabbitPass);
                    });
                }

                // Two-tier resilience:
                //   1) In-process exponential retry — fast healing of transient broker/db blips.
                //   2) Anything still failing after retries lands in the *_error queue (dead-letter)
                //      where it can be inspected in the RabbitMQ UI and shovelled back manually.
                //
                // Long-tail delayed redelivery (e.g. retry in 5/15 minutes) is intentionally NOT
                // wired here: it requires the rabbitmq_delayed_message_exchange plugin or an
                // external scheduler (Quartz/Hangfire). For this demo, retries + dead-letter
                // is sufficient and avoids extra infrastructure.
                cfg.UseMessageRetry(r => r.Exponential(
                    retryLimit: 5,
                    minInterval: TimeSpan.FromMilliseconds(200),
                    maxInterval: TimeSpan.FromSeconds(10),
                    intervalDelta: TimeSpan.FromMilliseconds(500)));

                configureEndpoints?.Invoke(cfg, ctx);

                cfg.ConfigureEndpoints(ctx);
            });
        });

        return services;
    }
}
