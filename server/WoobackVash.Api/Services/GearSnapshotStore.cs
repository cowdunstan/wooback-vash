using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WoobackVash.Api.Data;
using WoobackVash.Api.Models;

namespace WoobackVash.Api.Services;

/// <summary>
/// Writes a character's gear snapshot, shared by the two things that produce one:
/// the attendance import (a whole report's roster, source "wcl") and the on-demand
/// refresh on the character sheet (Blizzard live gear, or a WCL fallback).
///
/// One row per (character, <see cref="CharacterGearSnapshot.WclReportCode"/>), upserted
/// in place. For WCL the ref is the real report code; for Blizzard it is the constant
/// "blizzard" sentinel, so a character keeps exactly one continually-refreshed live row
/// rather than accreting history on every button press.
/// </summary>
public static class GearSnapshotStore
{
    /// <summary>Upserts one snapshot from parsed gear. Does not save — the caller owns
    /// the SaveChanges so a batch import stays one round-trip. Returns the row, or null
    /// when the gear carried no items (nothing worth overwriting a good snapshot with).
    /// When <paramref name="refreshSetup"/> is set (the WCL paths, which also carry
    /// spec/role) the character's raid setup is refreshed the way the log import does.</summary>
    public static async Task<CharacterGearSnapshot?> UpsertAsync(
        AppDbContext db, Character ch, string source, string reportRef,
        Guid? raidEventId, DateTimeOffset recordedAt, PlayerGear gear, bool refreshSetup)
    {
        if (gear.Items.Count == 0) return null;

        var items = JsonSerializer.Serialize(gear.Items.Select(i => new
        {
            slot = i.Slot,
            id = i.Id,
            name = i.Name,
            icon = i.Icon,
            quality = i.Quality,
            ilvl = i.ItemLevel,
            enchant = i.Enchant,
            enchantName = i.EnchantName,
            tempEnchant = i.TempEnchant,
            tempEnchantName = i.TempEnchantName,
            gems = i.Gems
        }));

        var snap = await db.GearSnapshots
            .FirstOrDefaultAsync(s => s.CharacterId == ch.Id && s.WclReportCode == reportRef);
        if (snap is null)
        {
            snap = new CharacterGearSnapshot { CharacterId = ch.Id, WclReportCode = reportRef };
            db.GearSnapshots.Add(snap);
        }
        snap.Source = source;
        snap.RaidEventId = raidEventId;
        snap.Spec = gear.Spec;
        snap.ItemLevel = gear.ItemLevel;
        snap.Items = items;
        snap.RecordedAt = recordedAt;
        snap.ImportedAt = DateTimeOffset.UtcNow;

        // The character's current raid setup, as last seen in a log. Only the sources
        // that report a spec/role touch it — Blizzard's equipment route carries neither.
        if (refreshSetup)
        {
            if (!string.IsNullOrWhiteSpace(gear.Spec)) ch.Spec = gear.Spec;
            if (!string.IsNullOrWhiteSpace(gear.Role)) ch.Role = gear.Role;
            ch.Class ??= gear.Cls;
            if (gear.Spec is not null || gear.Role is not null) ch.SetupUpdatedAt = snap.ImportedAt;
        }

        return snap;
    }
}
