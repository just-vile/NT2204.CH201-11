using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Saga.ShippingService.Infrastructure;

// Design-time only: used by `dotnet ef migrations add` / `database update`.
public sealed class ShippingDbContextFactory : IDesignTimeDbContextFactory<ShippingDbContext>
{
    public ShippingDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? "Host=localhost;Port=5432;Database=shipping;Username=saga;Password=saga";

        var options = new DbContextOptionsBuilder<ShippingDbContext>()
            .UseNpgsql(cs, b => b.MigrationsHistoryTable("__ef_migrations_shipping"))
            .Options;

        return new ShippingDbContext(options);
    }
}
