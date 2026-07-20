using Microsoft.EntityFrameworkCore;
using WoobackVash.Api.Models;

namespace WoobackVash.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Member> Members => Set<Member>();
    public DbSet<Character> Characters => Set<Character>();
    public DbSet<RaidEvent> RaidEvents => Set<RaidEvent>();
    public DbSet<BoardLayout> BoardLayouts => Set<BoardLayout>();
    public DbSet<LootAward> LootAwards => Set<LootAward>();
    public DbSet<AttendanceRecord> Attendance => Set<AttendanceRecord>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Member>(e =>
        {
            e.HasIndex(m => m.DiscordUserId).IsUnique();
            e.HasMany(m => m.Characters)
             .WithOne(c => c.Member)
             .HasForeignKey(c => c.MemberId)
             .OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<Character>(e =>
        {
            e.HasIndex(c => c.Name);
        });

        b.Entity<RaidEvent>(e =>
        {
            // Unique only when present; Postgres treats multiple NULLs as distinct.
            e.HasIndex(r => r.RhEventId).IsUnique();
        });

        b.Entity<BoardLayout>(e =>
        {
            // Store the snapshot as jsonb, not text — lets us query into it later.
            e.Property(x => x.State).HasColumnType("jsonb");
            // One layout per storage key (event id or "default").
            e.HasIndex(x => x.Name).IsUnique();
            e.HasOne(x => x.RaidEvent)
             .WithMany(r => r.BoardLayouts)
             .HasForeignKey(x => x.RaidEventId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<LootAward>(e =>
        {
            e.HasOne(x => x.RaidEvent)
             .WithMany(r => r.LootAwards)
             .HasForeignKey(x => x.RaidEventId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Character)
             .WithMany()
             .HasForeignKey(x => x.CharacterId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<AttendanceRecord>(e =>
        {
            e.HasOne(x => x.RaidEvent)
             .WithMany(r => r.Attendance)
             .HasForeignKey(x => x.RaidEventId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Character)
             .WithMany()
             .HasForeignKey(x => x.CharacterId)
             .OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Status).HasConversion<string>();
        });
    }
}
