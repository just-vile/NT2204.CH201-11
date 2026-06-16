using MassTransit;
using Microsoft.EntityFrameworkCore;
using Saga.OrderService.Domain;

namespace Saga.OrderService.Infrastructure;

public class OrderDbContext : DbContext
{
    public OrderDbContext(DbContextOptions<OrderDbContext> options) : base(options) { }

    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Order>(e =>
        {
            e.ToTable("orders");
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<int>();
            e.Property(x => x.Stage).HasConversion<int>();
            e.Property(x => x.TotalAmount).HasColumnType("numeric(18,2)");
            e.Property(x => x.IdempotencyKey).HasMaxLength(200).IsRequired();
            e.HasIndex(x => x.IdempotencyKey).IsUnique();
            e.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<OrderLine>(e =>
        {
            e.ToTable("order_lines");
            e.HasKey(x => x.Id);
            e.Property(x => x.Sku).HasMaxLength(64).IsRequired();
            e.Property(x => x.UnitPrice).HasColumnType("numeric(18,2)");
        });

        b.AddInboxStateEntity();
        b.AddOutboxMessageEntity();
        b.AddOutboxStateEntity();
    }
}
