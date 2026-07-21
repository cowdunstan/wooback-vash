namespace WoobackVash.Api.Models;

/// <summary>
/// A WoW character. Linked to a <see cref="Member"/> (nullable so a character
/// can exist before it is claimed). Exactly one per member is the main; the rest
/// are alts. This is the "Discord ↔ WoW main ↔ alts" link the guild wants.
/// </summary>
public class Character
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? MemberId { get; set; }
    public Member? Member { get; set; }

    public required string Name { get; set; }
    public string? Class { get; set; }
    public string? Spec { get; set; }
    public string? Realm { get; set; }

    /// <summary>True for the member's main character; alts are false.</summary>
    public bool IsMain { get; set; }

    public string? Notes { get; set; }

    /// <summary>
    /// "Not a guild character" — a bank alt, a transferred toon, an ex-guildie. Ignored
    /// characters are hidden from the roster cards and the unlinked list, and their loot
    /// and attendance are excluded from the history/stats pages. Nothing is deleted, so
    /// un-ignoring restores everything.
    /// </summary>
    public bool Ignored { get; set; }

    /// <summary>Guild this character was in at the last Blizzard sync; null means it was
    /// not on the guild roster. Only meaningful once <see cref="GuildSyncedAt"/> is set.</summary>
    public string? GuildName { get; set; }

    /// <summary>Guild rank from the Blizzard roster (0 = guild master).</summary>
    public int? GuildRank { get; set; }

    /// <summary>When the Blizzard guild sync last looked at this character. Null means
    /// never synced — the UI shows "unknown" rather than "not in guild".</summary>
    public DateTimeOffset? GuildSyncedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
