using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Saga.OrderService.Infrastructure;

// Design-time only: used by `dotnet ef migrations add` / `database update`.
// Not exercised at runtime — the host registers OrderDbContext via DI with the real connection string.
public sealed class OrderDbContextFactory : IDesignTimeDbContextFactory<OrderDbContext>
{
    public OrderDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? "Host=localhost;Port=5432;Database=orders;Username=saga;Password=saga";

        var options = new DbContextOptionsBuilder<OrderDbContext>()
            .UseNpgsql(cs, b => b.MigrationsHistoryTable("__ef_migrations_orders"))
            .Options;

        return new OrderDbContext(options);
    }
}
