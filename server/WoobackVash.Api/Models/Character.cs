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

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
