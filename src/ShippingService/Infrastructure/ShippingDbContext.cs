using MassTransit;
using Microsoft.EntityFrameworkCore;
using Saga.ShippingService.Domain;

namespace Saga.ShippingService.Infrastructure;

public class ShippingDbContext : DbContext
{
    public ShippingDbContext(DbContextOptions<ShippingDbContext> options) : base(options) { }

    public DbSet<Shipment> Shipments => Set<Shipment>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Shipment>(e =>
        {
            e.ToTable("shipments");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.OrderId).IsUnique();
            e.Property(x => x.Status).HasConversion<int>();
            e.Property(x => x.TrackingNumber).HasMaxLength(64).IsRequired();
        });

        b.AddInboxStateEntity();
        b.AddOutboxMessageEntity();
        b.AddOutboxStateEntity();
    }
}
