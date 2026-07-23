namespace WoobackVash.Api.Models;

/// <summary>
/// One equipped item, normalized from whatever source read it — a Warcraft Logs
/// combatantInfo or a Blizzard character-equipment profile. Both name the item and
/// its enchants for us (the enchant name is the only place that text exists — an
/// enchant id is not a spell id, so nothing downstream could resolve it), while gems
/// come as bare item ids, which the sheet hands to Wowhead. This is the shape the
/// gear snapshots store (see <see cref="CharacterGearSnapshot.Items"/>).
/// </summary>
public record GearItem(string Slot, long Id, string? Name, string? Icon, int? Quality,
    double? ItemLevel, long? Enchant, string? EnchantName,
    long? TempEnchant, string? TempEnchantName, List<long> Gems);

/// <summary>What one character was wearing, plus the spec/role the source gives it
/// (Blizzard's equipment route carries no spec, so those are null there).</summary>
public record PlayerGear(string Name, string? Realm, string? Cls, string? Spec,
    string? Role, double? ItemLevel, List<GearItem> Items);
