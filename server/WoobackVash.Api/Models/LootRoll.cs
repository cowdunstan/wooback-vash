namespace WoobackVash.Api.Models;

/// <summary>
/// One bid on a <see cref="LootAward"/> — a row from Gargul's Rolls array. Linked to
/// the bidding <see cref="Character"/> (find-or-create) so bid history is queryable per
/// character, not just per item.
/// </summary>
public class LootRoll
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid LootAwardId { get; set; }
    public LootAward? LootAward { get; set; }

    /// <summary>The character who placed the bid.</summary>
    public Guid CharacterId { get; set; }
    public Character? Character { get; set; }

    public int Amount { get; set; }

    /// <summary>Gargul roll classification, e.g. "MS" or "OS".</summary>
    public string? Classification { get; set; }

    public int? Priority { get; set; }
    public int? PlusOneState { get; set; }

    /// <summary>The roll's reported class string (e.g. "warrior"); seeds the bidder's class.</summary>
    public string? Class { get; set; }

    public DateTimeOffset RolledAt { get; set; }
}
