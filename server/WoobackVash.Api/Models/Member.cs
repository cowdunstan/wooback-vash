namespace WoobackVash.Api.Models;

/// <summary>
/// One row per Discord user. Upserted at OAuth login (Phase 1) keyed on
/// <see cref="DiscordUserId"/>. Owns the player's WoW characters (main + alts).
/// </summary>
public class Member
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Discord snowflake — the stable identity we key on.</summary>
    public required string DiscordUserId { get; set; }

    /// <summary>Discord username at last login (display only; can change).</summary>
    public string? DiscordUsername { get; set; }

    /// <summary>Preferred display name (global_name, falls back to username).</summary>
    public string? DisplayName { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;

    public List<Character> Characters { get; set; } = new();
}
