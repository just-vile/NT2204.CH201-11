using MassTransit;
using Microsoft.EntityFrameworkCore;
using Saga.InventoryService.Domain;

namespace Saga.InventoryService.Infrastructure;

public class InventoryDbContext : DbContext
{
    public InventoryDbContext(DbContextOptions<InventoryDbContext> options) : base(options) { }

    public DbSet<ProductStock> Stock => Set<ProductStock>();
    public DbSet<Reservation> Reservations => Set<Reservation>();
    public DbSet<ReservationLine> ReservationLines => Set<ReservationLine>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<ProductStock>(e =>
        {
            e.ToTable("product_stock");
            e.HasKey(x => x.Sku);
            e.Property(x => x.Sku).HasMaxLength(64);
        });

        b.Entity<Reservation>(e =>
        {
            e.ToTable("reservations");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.OrderId).IsUnique();
            e.Property(x => x.Status).HasConversion<int>();
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.ReservationId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ReservationLine>(e =>
        {
            e.ToTable("reservation_lines");
            e.HasKey(x => x.Id);
            e.Property(x => x.Sku).HasMaxLength(64).IsRequired();
        });

        b.AddInboxStateEntity();
        b.AddOutboxMessageEntity();
        b.AddOutboxStateEntity();
    }
}
