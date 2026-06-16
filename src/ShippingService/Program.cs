using Microsoft.EntityFrameworkCore;
using Saga.Shared.Infrastructure;
using Saga.ShippingService.Consumers;
using Saga.ShippingService.Infrastructure;

const string ServiceName = "shipping-service";

var builder = WebApplication.CreateBuilder(args);
builder.UseSagaSerilog(ServiceName);

builder.Services.AddDbContext<ShippingDbContext>((sp, opt) =>
{
    var cs = builder.Configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException("ConnectionStrings:Postgres missing");
    opt.UseNpgsql(cs, b => b.MigrationsHistoryTable("__ef_migrations_shipping"));
});

builder.Services.AddSagaJsonDefaults();
builder.Services.AddSagaOpenTelemetry(builder.Configuration, ServiceName);
builder.Services.AddSagaHealthChecks(builder.Configuration);

builder.Services.AddSagaMassTransit<ShippingDbContext>(
    builder.Configuration,
    ServiceName,
    cfg =>
    {
        cfg.AddConsumer<PaymentSucceededConsumer>();
    });

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ShippingDbContext>();
    await db.Database.MigrateAsync();
}

app.UseCorrelationId();
app.UseSagaPrometheusEndpoint();
app.MapSagaHealthChecks();

app.MapGet("/shipments/by-order/{orderId:guid}", async (Guid orderId, ShippingDbContext db, CancellationToken ct) =>
{
    var s = await db.Shipments.AsNoTracking().FirstOrDefaultAsync(x => x.OrderId == orderId, ct);
    return s is null ? Results.NotFound() : Results.Ok(s);
});

app.Run();

public partial class Program;
