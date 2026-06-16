extern alias OrderSvc;
extern alias PaymentSvc;
extern alias InventorySvc;
extern alias ShippingSvc;

using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Saga.Shared.Contracts;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Xunit;

namespace Saga.IntegrationTests.Infrastructure;

/// <summary>
/// Class fixture: boots Postgres + RabbitMQ once (via Testcontainers), creates the
/// four service databases, and launches the four services in-process via
/// <see cref="WebApplicationFactory{TEntryPoint}"/>. Also wires an out-of-band
/// <see cref="EventCollector"/> bus that observes saga events on the same broker.
/// </summary>
public sealed class SagaTestFixture : IAsyncLifetime
{
    private const string PgUser = "saga";
    private const string PgPass = "saga";
    private const string RabbitUser = "saga";
    private const string RabbitPass = "saga";

    private PostgreSqlContainer _postgres = default!;
    private RabbitMqContainer _rabbit = default!;

    private WebApplicationFactory<OrderSvc::Program>? _orderFactory;
    private WebApplicationFactory<PaymentSvc::Program>? _paymentFactory;
    private WebApplicationFactory<InventorySvc::Program>? _inventoryFactory;
    private WebApplicationFactory<ShippingSvc::Program>? _shippingFactory;

    private IBusControl? _collectorBus;
    private TerminalMetricCollector? _terminalMetrics;

    public HttpClient OrderClient { get; private set; } = default!;
    public HttpClient PaymentClient { get; private set; } = default!;
    public HttpClient InventoryClient { get; private set; } = default!;
    public HttpClient ShippingClient { get; private set; } = default!;
    public EventCollector Collector { get; } = new();
    public TerminalMetricCollector TerminalMetrics => _terminalMetrics
        ?? throw new InvalidOperationException("TerminalMetrics is only available after InitializeAsync().");

    /// <summary>Publish an event onto the shared broker (e.g. to inject OrderCancelled directly in a test).</summary>
    public Task PublishAsync<T>(T message) where T : class
        => _collectorBus!.Publish(message);

    public async Task InitializeAsync()
    {
        // ---- 1. Containers ----
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:16")
            .WithUsername(PgUser)
            .WithPassword(PgPass)
            .WithDatabase("postgres")
            .Build();

        _rabbit = new RabbitMqBuilder()
            .WithImage("rabbitmq:3.13-management")
            .WithUsername(RabbitUser)
            .WithPassword(RabbitPass)
            .Build();

        await Task.WhenAll(_postgres.StartAsync(), _rabbit.StartAsync());

        await CreateDatabasesAsync("orders", "payments", "inventory", "shipping");

        // Start the saga.terminal MeterListener BEFORE any service host boots so the
        // listener observes the InstrumentPublished event when SagaMetrics' static
        // counter is created.
        _terminalMetrics = new TerminalMetricCollector();

        // ---- 2. Resolve broker host/port for the test client side.
        // Testcontainers maps each container port to a random host port. When the test
        // process itself runs inside a sibling container (docker-out-of-docker), the
        // hostname is whatever TESTCONTAINERS_HOST_OVERRIDE resolves to (typically
        // host.docker.internal on Docker Desktop). _xxx.Hostname returns "localhost"
        // in that scenario which is wrong, so prefer the override when present.
        var hostOverride = Environment.GetEnvironmentVariable("TESTCONTAINERS_HOST_OVERRIDE");
        var rabbitHost = !string.IsNullOrEmpty(hostOverride) ? hostOverride : _rabbit.Hostname;
        var rabbitPort = _rabbit.GetMappedPublicPort(5672);
        var pgHost = !string.IsNullOrEmpty(hostOverride) ? hostOverride : _postgres.Hostname;
        var pgPort = _postgres.GetMappedPublicPort(5432);

        // ---- 3. Service factories ----
        _orderFactory = BuildFactory<OrderSvc::Program>(
            "OrderService",
            ConnString(pgHost, pgPort, "orders"),
            rabbitHost, rabbitPort,
            extras: new()
            {
                ["Saga:Timeout:Total"] = "00:00:08",
                ["Saga:Timeout:ScanInterval"] = "00:00:01"
            });

        _paymentFactory = BuildFactory<PaymentSvc::Program>(
            "PaymentService",
            ConnString(pgHost, pgPort, "payments"),
            rabbitHost, rabbitPort);

        _inventoryFactory = BuildFactory<InventorySvc::Program>(
            "InventoryService",
            ConnString(pgHost, pgPort, "inventory"),
            rabbitHost, rabbitPort);

        _shippingFactory = BuildFactory<ShippingSvc::Program>(
            "ShippingService",
            ConnString(pgHost, pgPort, "shipping"),
            rabbitHost, rabbitPort);

        // CreateClient() triggers host startup -> MigrateAsync() runs -> MassTransit connects.
        OrderClient = _orderFactory.CreateClient();
        PaymentClient = _paymentFactory.CreateClient();
        InventoryClient = _inventoryFactory.CreateClient();
        ShippingClient = _shippingFactory.CreateClient();

        // ---- 4. Out-of-band event collector bus on the same broker ----
        _collectorBus = Bus.Factory.CreateUsingRabbitMq(cfg =>
        {
            cfg.Host(rabbitHost, (ushort)rabbitPort, "/", h =>
            {
                h.Username(RabbitUser);
                h.Password(RabbitPass);
            });

            AddCollectorEndpoint<OrderPlaced>(cfg);
            AddCollectorEndpoint<OrderCompleted>(cfg);
            AddCollectorEndpoint<OrderCancelled>(cfg);
            AddCollectorEndpoint<PaymentSucceeded>(cfg);
            AddCollectorEndpoint<PaymentFailed>(cfg);
            AddCollectorEndpoint<PaymentRefunded>(cfg);
            AddCollectorEndpoint<InventoryReserved>(cfg);
            AddCollectorEndpoint<InventoryUnavailable>(cfg);
            AddCollectorEndpoint<InventoryReleased>(cfg);
            AddCollectorEndpoint<ShipmentDispatched>(cfg);
        });

        await _collectorBus.StartAsync();
    }

    public async Task DisposeAsync()
    {
        _terminalMetrics?.Dispose();

        if (_collectorBus is not null)
        {
            try { await _collectorBus.StopAsync(TimeSpan.FromSeconds(5)); } catch { /* ignore */ }
        }

        OrderClient?.Dispose();
        PaymentClient?.Dispose();
        InventoryClient?.Dispose();
        ShippingClient?.Dispose();

        if (_orderFactory is not null) await _orderFactory.DisposeAsync();
        if (_paymentFactory is not null) await _paymentFactory.DisposeAsync();
        if (_inventoryFactory is not null) await _inventoryFactory.DisposeAsync();
        if (_shippingFactory is not null) await _shippingFactory.DisposeAsync();

        await Task.WhenAll(_rabbit.DisposeAsync().AsTask(), _postgres.DisposeAsync().AsTask());
    }

    // --------------------------------------------------------------------
    // Helpers
    // --------------------------------------------------------------------

    private WebApplicationFactory<TEntry> BuildFactory<TEntry>(
        string serviceFolder,
        string postgresConnString,
        string rabbitHost,
        int rabbitPort,
        Dictionary<string, string?>? extras = null)
        where TEntry : class
    {
        var contentRoot = LocateServiceContentRoot(serviceFolder);

        var overrides = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = postgresConnString,
            ["RabbitMq:Host"] = rabbitHost,
            ["RabbitMq:Port"] = rabbitPort.ToString(),
            ["RabbitMq:Username"] = RabbitUser,
            ["RabbitMq:Password"] = RabbitPass,
            ["RabbitMq:VirtualHost"] = "/",
            // Point OTLP at a closed port so the exporter fails silently in the background
            // and never blocks startup or talks to a real collector.
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://127.0.0.1:1",
            ["Otel:Endpoint"] = "http://127.0.0.1:1"
        };

        if (extras is not null)
        {
            foreach (var (k, v) in extras) overrides[k] = v;
        }

        return new SagaWebAppFactory<TEntry>(contentRoot, overrides);
    }

    private static string ConnString(string host, int port, string db)
        => $"Host={host};Port={port};Database={db};Username={PgUser};Password={PgPass}";

    private async Task CreateDatabasesAsync(params string[] names)
    {
        var cs = $"Host={_postgres.Hostname};Port={_postgres.GetMappedPublicPort(5432)};Database=postgres;Username={PgUser};Password={PgPass}";
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        foreach (var name in names)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE \"{name}\"";
            try { await cmd.ExecuteNonQueryAsync(); }
            catch (PostgresException ex) when (ex.SqlState == "42P04") { /* already exists */ }
        }
    }

    private void AddCollectorEndpoint<T>(IRabbitMqBusFactoryConfigurator cfg) where T : class
    {
        cfg.ReceiveEndpoint($"test-events-{typeof(T).Name}", e =>
        {
            e.Handler<T>(ctx =>
            {
                Collector.Record(ctx);
                return Task.CompletedTask;
            });
        });
    }

    private static string LocateServiceContentRoot(string serviceFolder)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Saga.sln")))
            dir = dir.Parent;
        if (dir is null)
            throw new InvalidOperationException("Could not locate Saga.sln to anchor service content roots.");
        var path = Path.Combine(dir.FullName, "src", serviceFolder);
        if (!Directory.Exists(path))
            throw new InvalidOperationException($"Service content root not found: {path}");
        return path;
    }

    private sealed class SagaWebAppFactory<TEntry> : WebApplicationFactory<TEntry> where TEntry : class
    {
        private readonly string _contentRoot;
        private readonly Dictionary<string, string?> _overrides;

        public SagaWebAppFactory(string contentRoot, Dictionary<string, string?> overrides)
        {
            _contentRoot = contentRoot;
            _overrides = overrides;
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseContentRoot(_contentRoot);
            return base.CreateHost(builder);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(_contentRoot);
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, conf) =>
            {
                conf.AddInMemoryCollection(_overrides);
            });
            builder.ConfigureServices(services =>
            {
                // Quiet down the test output a bit; structured JSON serilog is noisy.
                services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
            });
        }
    }
}

[CollectionDefinition("Saga")]
public sealed class SagaCollection : ICollectionFixture<SagaTestFixture> { }
