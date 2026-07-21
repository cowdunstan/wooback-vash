namespace WoobackVash.Api.Models;

/// <summary>
/// A single item awarded on a raid night. Originally a hand-typed record hung off a
/// <see cref="RaidEvent"/>; now also the target of Gargul exports, so it stands alone —
/// the raid event and winning character are both optional (a Gargul import carries no
/// event key, and a disenchant has no winner). Gargul's <see cref="Checksum"/> makes
/// re-import idempotent, and each bid is captured as a <see cref="LootRoll"/>.
/// </summary>
public class LootAward
{
    public Guid Id { get; set; } = Guid.NewGuid();

    // Optional: manual awards attach to an event key; Gargul imports don't.
    public Guid? RaidEventId { get; set; }
    public RaidEvent? RaidEvent { get; set; }

    // Optional: null for a disenchant (Disenchanted == true).
    public Guid? CharacterId { get; set; }
    public Character? Character { get; set; }

    public required string ItemName { get; set; }
    public long? ItemId { get; set; }
    public string? Note { get; set; }

    /// <summary>Master looter who handed out the item, e.g. "Xatarina-Dreamscythe".</summary>
    public string? AwardedBy { get; set; }

    /// <summary>Gargul's stable per-award id. Unique — drives idempotent re-import.</summary>
    public string? Checksum { get; set; }

    /// <summary>True when the item was disenchanted (Gargul awardedTo == "|de|").</summary>
    public bool Disenchanted { get; set; }

    // Faithful Gargul metadata, kept for fidelity.
    public int? WinnerClass { get; set; }
    public bool OffSpec { get; set; }     // OS
    public bool SoftReserve { get; set; } // SR
    public bool Tmb { get; set; }         // TMB
    public bool Wishlist { get; set; }    // WL
    public bool PlusOne { get; set; }     // PL

    /// <summary>The bids on this item (Gargul Rolls), one row per player.</summary>
    public List<LootRoll> Rolls { get; set; } = new();

    public Guid? AwardedByMemberId { get; set; }
    public DateTimeOffset AwardedAt { get; set; } = DateTimeOffset.UtcNow;
}
