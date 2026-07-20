namespace WoobackVash.Api.Models;

/// <summary>
/// The Vash board state for a raid event. <see cref="State"/> is a JSON snapshot
/// that mirrors the in-browser shape in app.js
/// (<c>{ roster:[{id,name,cls,spec,status}], assignments:{range,healer,chaser} }</c>),
/// so saving/loading is lossless without an up-front normalization. Saved
/// explicitly via the board's "Save layout" button (Phase 3).
/// </summary>
public class BoardLayout
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? RaidEventId { get; set; }
    public RaidEvent? RaidEvent { get; set; }

    /// <summary>
    /// The board's storage key, unique. Either a Raid-Helper event id (when the
    /// board was built from an event) or the literal "default" for a manually
    /// built board. The frontend chooses it; see BoardEndpoints.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>Raw board snapshot, stored as jsonb (see AppDbContext config).</summary>
    public string State { get; set; } = "{}";

    public Guid? UpdatedByMemberId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
