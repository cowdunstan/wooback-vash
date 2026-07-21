namespace WoobackVash.Api.Models;

/// <summary>
/// What a character was wearing on one raid night — items, enchants and gems —
/// captured from a Warcraft Logs report when officers import its attendance.
/// One row per (character, report), so re-importing a report updates in place.
///
/// The log is the source because Blizzard's character-profile routes are not
/// dependable on the Anniversary realms (the guild sync in BlizzardService uses a
/// Game Data route, which is a different API). <see cref="Source"/> records where
/// a snapshot came from so another source can be added without a schema change.
/// </summary>
public class CharacterGearSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid CharacterId { get; set; }
    public Character? Character { get; set; }

    /// <summary>The raid this gear was worn on, when the report resolved to one.</summary>
    public Guid? RaidEventId { get; set; }
    public RaidEvent? RaidEvent { get; set; }

    /// <summary>Warcraft Logs report code the snapshot was read from.</summary>
    public required string WclReportCode { get; set; }

    /// <summary>Where the snapshot came from — "wcl" today.</summary>
    public string Source { get; set; } = "wcl";

    /// <summary>Spec the log reported for this character on the night.</summary>
    public string? Spec { get; set; }

    /// <summary>Average item level, when the log reports one.</summary>
    public double? ItemLevel { get; set; }

    /// <summary>
    /// The equipped items, stored as jsonb (see AppDbContext config). Normalized
    /// from the log's combatantInfo.gear:
    /// <c>[{ slot, id, name, quality, ilvl, enchant, tempEnchant, gems:[id,…] }]</c>.
    /// Ids are all the log carries — names and stats come from Wowhead tooltips
    /// on the character sheet.
    /// </summary>
    public string Items { get; set; } = "[]";

    /// <summary>When the raid was — the report's start time, so snapshots sort by night.</summary>
    public DateTimeOffset RecordedAt { get; set; }

    /// <summary>When the import last wrote this row.</summary>
    public DateTimeOffset ImportedAt { get; set; } = DateTimeOffset.UtcNow;
}
