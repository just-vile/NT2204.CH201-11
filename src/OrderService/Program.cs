using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Saga.OrderService.Consumers;
using Saga.OrderService.Domain;
using Saga.OrderService.Infrastructure;
using Saga.OrderService.Saga;
using Saga.Shared.Contracts;
using Saga.Shared.Infrastructure;

const string ServiceName = "order-service";

var builder = WebApplication.CreateBuilder(args);
builder.UseSagaSerilog(ServiceName);

builder.Services.AddDbContext<OrderDbContext>((sp, opt) =>
{
    var cs = builder.Configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException("ConnectionStrings:Postgres missing");
    opt.UseNpgsql(cs, b => b.MigrationsHistoryTable("__ef_migrations_orders"));
});

builder.Services.AddSagaJsonDefaults();
builder.Services.AddSagaOpenTelemetry(builder.Configuration, ServiceName);
builder.Services.AddSagaHealthChecks(builder.Configuration);

builder.Services.AddOptions<SagaTimeoutOptions>()
    .Bind(builder.Configuration.GetSection(SagaTimeoutOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSagaMassTransit<OrderDbContext>(
    builder.Configuration,
    ServiceName,
    cfg =>
    {
        cfg.AddConsumer<PaymentFailedConsumer>();
        cfg.AddConsumer<InventoryUnavailableConsumer>();
        cfg.AddConsumer<ShipmentDispatchedConsumer>();
        cfg.AddConsumer<PaymentSucceededStageTracker>();
        cfg.AddConsumer<InventoryReservedStageTracker>();
    });

builder.Services.AddHostedService<OrderTimeoutWatchdog>();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
    await db.Database.MigrateAsync();
}

app.UseCorrelationId();
app.UseSagaPrometheusEndpoint();
app.MapSagaHealthChecks();

app.MapPost("/orders", async (
    [FromBody] PlaceOrderRequest req,
    HttpContext httpCtx,
    OrderDbContext db,
    IPublishEndpoint publish,
    ILogger<Program> log,
    CancellationToken ct) =>
{
    if (!httpCtx.Request.Headers.TryGetValue("Idempotency-Key", out var keyValues)
        || string.IsNullOrWhiteSpace(keyValues.ToString()))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["idempotency-key"] = ["header is required"]
        });
    }

    var idempotencyKey = keyValues.ToString();
    if (idempotencyKey.Length > 200)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["idempotency-key"] = ["header must be at most 200 characters"]
        });
    }

    var existing = await db.Orders.AsNoTracking()
        .FirstOrDefaultAsync(o => o.IdempotencyKey == idempotencyKey, ct);
    if (existing is not null)
    {
        return Results.Ok(new
        {
            Id = existing.Id,
            Status = OrderStatus.Pending,
            CorrelationId = existing.CorrelationId
        });
    }

    if (req.Items is null || req.Items.Count == 0)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["items"] = ["at least one item required"] });

    var invalid = req.Items
        .Select((it, idx) => (idx, error: ValidateItem(it)))
        .Where(t => t.error is not null)
        .ToDictionary(t => $"items[{t.idx}]", t => new[] { t.error! });
    if (invalid.Count > 0) return Results.ValidationProblem(invalid);

    var correlationId = httpCtx.GetCorrelationId();
    var order = new Order
    {
        Id = Guid.NewGuid(),
        CustomerId = req.CustomerId,
        TotalAmount = req.Items.Sum(i => i.Quantity * i.UnitPrice),
        CorrelationId = correlationId,
        IdempotencyKey = idempotencyKey,
        Items = req.Items.Select(i => new OrderLine { Sku = i.Sku, Quantity = i.Quantity, UnitPrice = i.UnitPrice }).ToList()
    };
    db.Orders.Add(order);

    await publish.Publish(new OrderPlaced(
        order.Id,
        order.CustomerId,
        order.Items.Select(i => new OrderItem(i.Sku, i.Quantity, i.UnitPrice)).ToList(),
        order.TotalAmount,
        correlationId,
        DateTimeOffset.UtcNow), ct);

    try
    {
        await db.SaveChangesAsync(ct);
    }
    catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
    {
        var winner = await db.Orders.AsNoTracking()
            .FirstOrDefaultAsync(o => o.IdempotencyKey == idempotencyKey, ct);
        if (winner is null) throw;
        return Results.Ok(new
        {
            Id = winner.Id,
            Status = OrderStatus.Pending,
            CorrelationId = winner.CorrelationId
        });
    }
    log.LogInformation("Order {OrderId} placed by customer {CustomerId}", order.Id, order.CustomerId);

    return Results.Created($"/orders/{order.Id}", new { order.Id, order.Status, order.CorrelationId });
});

app.MapGet("/orders/{id:guid}", async (Guid id, OrderDbContext db, CancellationToken ct) =>
{
    var order = await db.Orders.AsNoTracking().Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == id, ct);
    return order is null ? Results.NotFound() : Results.Ok(order);
});

app.Run();

static string? ValidateItem(PlaceOrderItem item)
{
    if (string.IsNullOrWhiteSpace(item.Sku)) return "sku is required";
    if (item.Quantity <= 0) return "quantity must be > 0";
    if (item.UnitPrice < 0) return "unitPrice must be >= 0";
    return null;
}

public sealed record PlaceOrderRequest(Guid CustomerId, List<PlaceOrderItem> Items);
public sealed record PlaceOrderItem(string Sku, int Quantity, decimal UnitPrice);

public partial class Program;
