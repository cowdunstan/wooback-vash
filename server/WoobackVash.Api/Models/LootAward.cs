namespace WoobackVash.Api.Models;

/// <summary>A single item awarded to a character on a raid night (Phase 4).</summary>
public class LootAward
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RaidEventId { get; set; }
    public RaidEvent? RaidEvent { get; set; }

    public Guid CharacterId { get; set; }
    public Character? Character { get; set; }

    public required string ItemName { get; set; }
    public long? ItemId { get; set; }
    public string? Note { get; set; }

    public Guid? AwardedByMemberId { get; set; }
    public DateTimeOffset AwardedAt { get; set; } = DateTimeOffset.UtcNow;
}
