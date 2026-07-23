namespace WoobackVash.Api.Models;

/// <summary>
/// What a character was wearing — items, enchants and gems. Two things write one:
/// the attendance import (a whole report's roster, one snapshot per raid night) and
/// the on-demand "refresh gear" on the character sheet. One row per
/// (character, <see cref="WclReportCode"/>), upserted in place.
///
/// <see cref="Source"/> records where a snapshot came from, so a source can be added
/// without a schema change — which is exactly what the refresh does. It tries
/// Blizzard's live character-equipment route first (source "blizzard") and falls back
/// to the character's most recent Warcraft Logs report (source "wcl") when Blizzard
/// fails, as its character-profile routes are the less dependable ones on the
/// Anniversary realms. A Blizzard snapshot has no report, so it is keyed by the constant
/// "blizzard" sentinel in <see cref="WclReportCode"/> — one continually-refreshed live
/// row per character rather than a new one each button press. (The guild sync in
/// BlizzardService uses a Game Data route, a different, dependable API.)
/// </summary>
public class CharacterGearSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid CharacterId { get; set; }
    public Character? Character { get; set; }

    /// <summary>The raid this gear was worn on, when the report resolved to one.</summary>
    public Guid? RaidEventId { get; set; }
    public RaidEvent? RaidEvent { get; set; }

    /// <summary>The snapshot's key within a character: a Warcraft Logs report code for
    /// a "wcl" snapshot, or the constant "blizzard" sentinel for a live Blizzard one
    /// (which has no report). Part of the unique index with the character.</summary>
    public required string WclReportCode { get; set; }

    /// <summary>Where the snapshot came from — "wcl" (a log) or "blizzard" (live).</summary>
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
