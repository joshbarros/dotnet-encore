using Encore.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Encore.Gigs;

public class GigsDbContext(DbContextOptions<GigsDbContext> options) : ModuleDbContext(options, SchemaName)
{
    public const string SchemaName = "gigs";

    public DbSet<Gig> Gigs => Set<Gig>();
    public DbSet<SeatHold> Holds => Set<SeatHold>();
    public DbSet<GigFan> Fans => Set<GigFan>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Gig>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Code).HasMaxLength(20);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Gigs_Remaining", "\"SeatsLeft\" >= 0 AND \"SeatsLeft\" <= \"SeatsTotal\"");
                t.HasCheckConstraint("CK_Gigs_Cap", "\"MaxPerFan\" >= 1");
            });
            e.Property(x => x.MaxPerFan).HasDefaultValue(2);
        });

        b.Entity<GigFan>(e =>
        {
            e.ToTable("GigFans", t => t.HasCheckConstraint("CK_GigFans_Seats", "\"Seats\" >= 0"));
            e.HasKey(x => new { x.GigId, x.OwnerId });
            e.HasOne<Gig>().WithMany().HasForeignKey(x => x.GigId);
        });

        b.Entity<SeatHold>(e =>
        {
            e.ToTable("SeatHolds");
            e.HasKey(x => x.OrderId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.HasOne<Gig>().WithMany().HasForeignKey(x => x.GigId);
        });
    }
}
