namespace WoobackVash.Api.Models;

public enum AttendanceStatus
{
    Present,
    Late,
    Bench,
    Absent
}

/// <summary>A character's attendance status for a raid night (Phase 4).</summary>
public class AttendanceRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RaidEventId { get; set; }
    public RaidEvent? RaidEvent { get; set; }

    public Guid CharacterId { get; set; }
    public Character? Character { get; set; }

    public AttendanceStatus Status { get; set; } = AttendanceStatus.Present;
    public string? Note { get; set; }

    public DateTimeOffset RecordedAt { get; set; } = DateTimeOffset.UtcNow;
}
