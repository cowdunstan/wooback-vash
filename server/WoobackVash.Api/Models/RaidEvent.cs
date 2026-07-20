namespace WoobackVash.Api.Models;

/// <summary>
/// A raid night. Usually mirrors a Raid-Helper event (<see cref="RhEventId"/>),
/// but can be created manually. Board layouts, loot, and attendance hang off it.
/// </summary>
public class RaidEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Raid-Helper event id, when this event came from Raid-Helper. Unique.</summary>
    public string? RhEventId { get; set; }

    /// <summary>Warcraft Logs report code, when this event was imported from a WCL
    /// log (the attendance app keys on this). Unique when present.</summary>
    public string? WclReportCode { get; set; }

    public string? Title { get; set; }
    public DateTimeOffset? StartsAt { get; set; }
    public string? Zone { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<BoardLayout> BoardLayouts { get; set; } = new();
    public List<LootAward> LootAwards { get; set; } = new();
    public List<AttendanceRecord> Attendance { get; set; } = new();
}
