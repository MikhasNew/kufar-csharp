using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using RealEstateMinsk.Models;

namespace RealEstateMinsk.Data;

public class AppDbContext : DbContext
{
    public DbSet<Listing> Listings { get; set; } = null!;
    public DbSet<PriceHistory> PriceHistories { get; set; } = null!;
    public DbSet<InvestmentScore> InvestmentScores { get; set; } = null!;
    public DbSet<Alert> Alerts { get; set; } = null!;
    public DbSet<PointOfInterest> PointsOfInterest { get; set; } = null!;

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Listing>()
            .HasIndex(l => l.ExternalId).IsUnique();

        modelBuilder.Entity<Listing>()
            .HasIndex(l => l.District);

        modelBuilder.Entity<Listing>()
            .HasIndex(l => l.PricePerSqm);

        modelBuilder.Entity<PriceHistory>()
            .HasIndex(p => p.ListingId);

        modelBuilder.Entity<InvestmentScore>()
            .HasOne(s => s.Listing)
            .WithMany()
            .HasForeignKey(s => s.ListingId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<InvestmentScore>()
            .HasIndex(s => s.ListingId);

        modelBuilder.Entity<InvestmentScore>()
            .HasIndex(s => s.TotalScore);

        modelBuilder.Entity<Alert>()
            .HasIndex(a => a.IsActive);

        modelBuilder.Entity<PointOfInterest>()
            .Property(p => p.GeoLocation)
            .HasColumnType("geometry(Point, 4326)");

        modelBuilder.Entity<PointOfInterest>()
            .HasIndex(p => p.Type);

        if (Database.IsNpgsql())
        {
            modelBuilder.Entity<Listing>()
                .Property(l => l.GeoLocation)
                .HasColumnType("geometry(Point, 4326)");

            modelBuilder.HasPostgresExtension("postgis");
        }
        else
        {
            modelBuilder.Entity<Listing>().Ignore(l => l.GeoLocation);
        }
    }
}