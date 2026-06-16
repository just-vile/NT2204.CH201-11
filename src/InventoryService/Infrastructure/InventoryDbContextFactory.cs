using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Saga.InventoryService.Infrastructure;

// Design-time only: used by `dotnet ef migrations add` / `database update`.
public sealed class InventoryDbContextFactory : IDesignTimeDbContextFactory<InventoryDbContext>
{
    public InventoryDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? "Host=localhost;Port=5432;Database=inventory;Username=saga;Password=saga";

        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseNpgsql(cs, b => b.MigrationsHistoryTable("__ef_migrations_inventory"))
            .Options;

        return new InventoryDbContext(options);
    }
}
