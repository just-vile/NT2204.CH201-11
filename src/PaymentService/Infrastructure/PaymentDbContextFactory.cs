using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Saga.PaymentService.Infrastructure;

// Design-time only: used by `dotnet ef migrations add` / `database update`.
public sealed class PaymentDbContextFactory : IDesignTimeDbContextFactory<PaymentDbContext>
{
    public PaymentDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? "Host=localhost;Port=5432;Database=payments;Username=saga;Password=saga";

        var options = new DbContextOptionsBuilder<PaymentDbContext>()
            .UseNpgsql(cs, b => b.MigrationsHistoryTable("__ef_migrations_payments"))
            .Options;

        return new PaymentDbContext(options);
    }
}
