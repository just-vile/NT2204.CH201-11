using Microsoft.EntityFrameworkCore;
using Saga.InventoryService.Consumers;
using Saga.InventoryService.Domain;
using Saga.InventoryService.Infrastructure;
using Saga.Shared.Infrastructure;

const string ServiceName = "inventory-service";

var builder = WebApplication.CreateBuilder(args);
builder.UseSagaSerilog(ServiceName);

builder.Services.AddDbContext<InventoryDbContext>((sp, opt) =>
{
    var cs = builder.Configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException("ConnectionStrings:Postgres missing");
    opt.UseNpgsql(cs, b => b.MigrationsHistoryTable("__ef_migrations_inventory"));
});

builder.Services.AddSagaJsonDefaults();
builder.Services.AddSagaOpenTelemetry(builder.Configuration, ServiceName);
builder.Services.AddSagaHealthChecks(builder.Configuration);

builder.Services.AddSagaMassTransit<InventoryDbContext>(
    builder.Configuration,
    ServiceName,
    cfg =>
    {
        cfg.AddConsumer<OrderPlacedConsumer>();
        cfg.AddConsumer<PaymentFailedConsumer>();
        cfg.AddConsumer<PaymentRefundedConsumer>();
        cfg.AddConsumer<OrderCancelledConsumer>();
    });

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
    await db.Database.MigrateAsync();
    if (!await db.Stock.AnyAsync())
    {
        db.Stock.AddRange(
            new ProductStock { Sku = "SKU-001", Available = 100 },
            new ProductStock { Sku = "SKU-002", Available = 50 },
            new ProductStock { Sku = "SKU-003", Available = 10 },
            new ProductStock { Sku = "OUT_OF_STOCK-X", Available = 0 });
        await db.SaveChangesAsync();
    }
}

app.UseCorrelationId();
app.UseSagaPrometheusEndpoint();
app.MapSagaHealthChecks();

app.MapGet("/stock/{sku}", async (string sku, InventoryDbContext db, CancellationToken ct) =>
{
    var s = await db.Stock.FindAsync(new object?[] { sku }, ct);
    return s is null ? Results.NotFound() : Results.Ok(s);
});

app.MapGet("/reservations/by-order/{orderId:guid}", async (Guid orderId, InventoryDbContext db, CancellationToken ct) =>
{
    var r = await db.Reservations.AsNoTracking().Include(x => x.Lines).FirstOrDefaultAsync(x => x.OrderId == orderId, ct);
    return r is null ? Results.NotFound() : Results.Ok(r);
});

app.Run();

public partial class Program;
