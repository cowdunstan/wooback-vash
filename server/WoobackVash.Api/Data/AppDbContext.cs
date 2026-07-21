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
    public DbSet<LootRoll> LootRolls => Set<LootRoll>();
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
            // Every roster/loot/attendance read filters ignored characters out.
            e.HasIndex(c => c.Ignored);
        });

        b.Entity<RaidEvent>(e =>
        {
            // Unique only when present; Postgres treats multiple NULLs as distinct.
            e.HasIndex(r => r.RhEventId).IsUnique();
            e.HasIndex(r => r.WclReportCode).IsUnique();
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
            // Both optional now: a Gargul import carries no event key, and a
            // disenchant has no winning character. Detach (don't cascade) on delete.
            e.HasOne(x => x.RaidEvent)
             .WithMany(r => r.LootAwards)
             .HasForeignKey(x => x.RaidEventId)
             .IsRequired(false)
             .OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Character)
             .WithMany()
             .HasForeignKey(x => x.CharacterId)
             .IsRequired(false)
             .OnDelete(DeleteBehavior.SetNull);
            // Gargul's per-award id — unique when present so re-import is idempotent.
            // Postgres treats multiple NULLs as distinct, so manual awards are unaffected.
            e.HasIndex(x => x.Checksum).IsUnique();
        });

        b.Entity<LootRoll>(e =>
        {
            e.HasOne(x => x.LootAward)
             .WithMany(a => a.Rolls)
             .HasForeignKey(x => x.LootAwardId)
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
