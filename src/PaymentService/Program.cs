using Microsoft.EntityFrameworkCore;
using Saga.PaymentService.Consumers;
using Saga.PaymentService.Infrastructure;
using Saga.Shared.Infrastructure;

const string ServiceName = "payment-service";

var builder = WebApplication.CreateBuilder(args);
builder.UseSagaSerilog(ServiceName);

builder.Services.AddDbContext<PaymentDbContext>((sp, opt) =>
{
    var cs = builder.Configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException("ConnectionStrings:Postgres missing");
    opt.UseNpgsql(cs, b => b.MigrationsHistoryTable("__ef_migrations_payments"));
});

builder.Services.AddSagaJsonDefaults();
builder.Services.AddSagaOpenTelemetry(builder.Configuration, ServiceName);
builder.Services.AddSagaHealthChecks(builder.Configuration);

builder.Services.AddSagaMassTransit<PaymentDbContext>(
    builder.Configuration,
    ServiceName,
    cfg =>
    {
        cfg.AddConsumer<InventoryReservedConsumer>();
        cfg.AddConsumer<InventoryUnavailableConsumer>();
        cfg.AddConsumer<OrderCancelledConsumer>();
    });

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
    await db.Database.MigrateAsync();
}

app.UseCorrelationId();
app.UseSagaPrometheusEndpoint();
app.MapSagaHealthChecks();

app.MapGet("/payments/by-order/{orderId:guid}", async (Guid orderId, PaymentDbContext db, CancellationToken ct) =>
{
    var p = await db.Payments.AsNoTracking().FirstOrDefaultAsync(x => x.OrderId == orderId, ct);
    return p is null ? Results.NotFound() : Results.Ok(p);
});

app.Run();

public partial class Program;
