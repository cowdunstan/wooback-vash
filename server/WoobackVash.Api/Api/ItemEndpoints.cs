using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WoobackVash.Api.Auth;
using WoobackVash.Api.Data;
using WoobackVash.Api.Models;
using WoobackVash.Api.Services;

namespace WoobackVash.Api.Api;

/// <summary>
/// The item page (item.html): everything the guild database knows about one item —
/// who is wearing it now, how often it has dropped, who won it and what everyone
/// rolled. The join the site never had: gear snapshots and loot awards meet here.
///
/// Session-gated, not officer-gated, like the character sheet and loot history.
/// </summary>
public static class ItemEndpoints
{
    public static void MapItemEndpoints(this IEndpointRouteBuilder app)
    {
        // ?id=<wowhead item id> | ?name=<item name>
        app.MapGet("/api/items", async (HttpContext ctx, SessionTokenService tokens) =>
        {
            var (_, error) = ctx.RequireSession(tokens);
            if (error is not null) return error;
            var db = ctx.RequestServices.GetService<AppDbContext>();
            if (db is null) return DbUnavailable();

            var idRaw = ctx.Request.Query["id"].ToString().Trim();
            var nameRaw = ctx.Request.Query["name"].ToString().Trim();
            long? id = long.TryParse(idRaw, out var parsed) ? parsed : null;
            if (id is null && string.IsNullOrEmpty(nameRaw))
                return Results.Json(new { error = "bad_request", detail = "An item id or name is required." },
                    statusCode: 400);

            // Awards for this item. Ignored characters are out of the guild, so their
            // history is excluded here exactly as it is on the loot pages; disenchants
            // carry no character and always stay.
            var awardQuery = db.LootAwards.AsNoTracking()
                .Where(l => l.Character == null || !l.Character.Ignored);

            // An id matches by id, plus the hand-typed rows that carry only a name —
            // so one page covers both ways the same item reached the database.
            var name = nameRaw;
            if (id is not null)
            {
                name = await db.LootAwards.AsNoTracking()
                    .Where(l => l.ItemId == id)
                    .Select(l => l.ItemName)
                    .FirstOrDefaultAsync() ?? "";
                awardQuery = name.Length > 0
                    ? awardQuery.Where(l => l.ItemId == id || (l.ItemId == null && l.ItemName.ToLower() == name.ToLower()))
                    : awardQuery.Where(l => l.ItemId == id);
            }
            else
            {
                var lower = nameRaw.ToLower();
                awardQuery = awardQuery.Where(l => l.ItemName.ToLower() == lower);
                // Adopt an id from any matching row, so the page still gets a tooltip.
                id = await awardQuery.Where(l => l.ItemId != null).Select(l => l.ItemId).FirstOrDefaultAsync();
            }

            // Same projection shape as LootRows in RaidLogEndpoints, so the frontend's
            // rolls markup is identical on both pages.
            var awards = await awardQuery
                .OrderByDescending(l => l.AwardedAt)
                .Select(l => new
                {
                    id = l.Id,
                    character = l.Character != null ? l.Character.Name : null,
                    characterId = l.CharacterId,
                    characterClass = l.Character != null ? l.Character.Class : null,
                    disenchanted = l.Disenchanted,
                    itemName = l.ItemName,
                    itemId = l.ItemId,
                    note = l.Note,
                    awardedBy = l.AwardedBy,
                    awardedAt = l.AwardedAt,
                    raid = l.RaidEvent != null ? l.RaidEvent.Title : null,
                    offSpec = l.OffSpec,
                    softReserve = l.SoftReserve,
                    tmb = l.Tmb,
                    wishlist = l.Wishlist,
                    rolls = l.Rolls
                        .Where(r => r.Character == null || !r.Character.Ignored)
                        .OrderByDescending(r => r.Amount)
                        .Select(r => new
                        {
                            player = r.Character != null ? r.Character.Name : null,
                            playerId = r.CharacterId,
                            cls = r.Class,
                            amount = r.Amount,
                            classification = r.Classification,
                            priority = r.Priority
                        })
                })
                .ToListAsync();

            var (equipped, gearItem) = id is null
                ? (new List<object>(), (JsonElement?)null)
                : await Equipped(db, id.Value);

            if (awards.Count == 0 && equipped.Count == 0)
                return Results.Json(new { error = "not_found", detail = "Nothing recorded for that item." },
                    statusCode: 404);

            // The log knows the icon and quality; a hand-typed award knows only a name.
            // A gem is only ever an id in a socket, so it has no name here at all —
            // the page's Wowhead link fills that in, and "Item <id>" is the fallback.
            var item = new
            {
                id,
                name = Str(gearItem, "name")
                       ?? (name.Length > 0 ? name : awards.FirstOrDefault()?.itemName)
                       ?? (nameRaw.Length > 0 ? nameRaw : $"Item {id}"),
                icon = Icon(Str(gearItem, "icon")),
                quality = Num(gearItem, "quality"),
                ilvl = Num(gearItem, "ilvl")
            };

            return Results.Json(new { item, drops = awards.Count, awards, equipped });
        });

        // Every item the guild is currently wearing (items.html). The inverse of the
        // route above: that one starts from an item, this one lists them all, with
        // who has each equipped and how often it has dropped.
        app.MapGet("/api/items/list", async (HttpContext ctx, SessionTokenService tokens) =>
        {
            var (_, error) = ctx.RequireSession(tokens);
            if (error is not null) return error;
            var db = ctx.RequestServices.GetService<AppDbContext>();
            if (db is null) return DbUnavailable();

            var snapshots = await LatestSnapshots(db);

            // Keyed by item id. Gems are skipped: the log carries them as bare ids
            // with no name, so they would list as "Item 32211" and never match a
            // search. Slot entries repeat when someone swapped mid-night, so each
            // character counts once per item.
            var items = new Dictionary<long, ItemRow>();
            foreach (var s in snapshots)
            {
                var seen = new HashSet<long>();
                foreach (var entry in ParseItems(s.Items).EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object) continue;
                    var id = (long?)Num(entry, "id");
                    if (id is null or <= 0 || !seen.Add(id.Value)) continue;

                    if (!items.TryGetValue(id.Value, out var row))
                        items[id.Value] = row = new ItemRow();
                    // A snapshot may omit any of these, so keep the first one that has it.
                    row.Name ??= Str(entry, "name");
                    row.Icon ??= Icon(Str(entry, "icon"));
                    row.Quality ??= Num(entry, "quality");
                    row.Ilvl ??= Num(entry, "ilvl");

                    var c = s.Character!;
                    if (!row.Wearers.Add(c.Id)) continue;
                    row.Equipped.Add((c.Name, new
                    {
                        characterId = c.Id,
                        name = c.Name,
                        cls = c.Class,
                        isMain = c.IsMain,
                        member = c.Member is null ? null : c.Member.Nickname ?? c.Member.DisplayName ?? c.Member.DiscordUsername,
                        slot = Str(entry, "slot")
                    }));
                }
            }

            // Drop counts, matched the same way the single-item route matches: by id,
            // plus the hand-typed rows that carry only a name. Ignored characters are
            // out of the guild, so their awards are excluded as they are everywhere else.
            var awarded = await db.LootAwards.AsNoTracking()
                .Where(l => l.Character == null || !l.Character.Ignored)
                .Select(l => new { l.ItemId, l.ItemName })
                .ToListAsync();
            var byId = awarded.Where(a => a.ItemId != null)
                .GroupBy(a => a.ItemId!.Value)
                .ToDictionary(g => g.Key, g => g.Count());
            var byName = awarded.Where(a => a.ItemId == null)
                .GroupBy(a => a.ItemName.ToLowerInvariant())
                .ToDictionary(g => g.Key, g => g.Count());

            var rows = items.Select(kv => new
            {
                id = kv.Key,
                name = kv.Value.Name ?? $"Item {kv.Key}",
                icon = kv.Value.Icon,
                quality = kv.Value.Quality,
                ilvl = kv.Value.Ilvl,
                drops = (byId.TryGetValue(kv.Key, out var n) ? n : 0)
                        + (kv.Value.Name is not null && byName.TryGetValue(kv.Value.Name.ToLowerInvariant(), out var m) ? m : 0),
                equipped = kv.Value.Equipped
                    .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(e => e.Row)
                    .ToList()
            })
            .OrderBy(r => r.name, StringComparer.OrdinalIgnoreCase)
            .ToList();

            return Results.Json(rows);
        });

        // Item names → item ids, for the loot-prio page's Gargul soft-reserve export
        // (the sheet has names, a soft reserve is keyed by id). Officer-gated: it is
        // an officer feature and it spends the guild's Blizzard API budget.
        //
        // POST rather than GET because a raid tab is ~150 names, which is more than
        // belongs in a query string. Names that can't be resolved come back listed
        // so the page can say which ones, rather than dropping them quietly.
        app.MapPost("/api/items/resolve", async (
            HttpContext ctx, SessionTokenService tokens, BlizzardService blizzard, ResolveInput input) =>
        {
            var (_, error) = ctx.RequireOfficer(tokens);
            if (error is not null) return error;

            if (input?.Names is null || input.Names.Count == 0)
                return Results.Json(new { error = "bad_request", detail = "No item names given." },
                                    statusCode: 400);
            if (input.Names.Count > 500)
                return Results.Json(new { error = "bad_request", detail = "Too many names in one call (max 500)." },
                                    statusCode: 400);

            var (status, ids, unresolved, err) = await blizzard.ResolveItemIdsAsync(input.Names);
            if (ids is null)
                return Results.Json(new { error = "upstream", detail = err }, statusCode: status);

            return Results.Json(new { resolved = ids, unresolved });
        });
    }

    /// <summary>Body of POST /api/items/resolve.</summary>
    public record ResolveInput(List<string> Names);

    /// <summary>One item as it is assembled across every character wearing it.</summary>
    private sealed class ItemRow
    {
        public string? Name;
        public string? Icon;
        public double? Quality;
        public double? Ilvl;
        public List<(string Name, object Row)> Equipped = new();
        /// <summary>Characters already listed, so nobody wears the same item twice.</summary>
        public HashSet<Guid> Wearers = new();
    }

    /// <summary>
    /// The most recent gear snapshot of every guild character — the one definition of
    /// "what people are wearing", shared by the item page and the item list. Exactly
    /// one snapshot per character, always.
    /// </summary>
    private static async Task<List<CharacterGearSnapshot>> LatestSnapshots(AppDbContext db)
    {
        var recent = await db.GearSnapshots.AsNoTracking()
            .Include(s => s.Character!).ThenInclude(c => c.Member)
            .Where(s => s.Character != null && !s.Character.Ignored)
            // Narrow to the latest per character in SQL: none newer exists for the same
            // one. Snapshots are keyed on (character, report), so two report codes over
            // the same pull window — a re-upload, or two raiders logging one night —
            // give a character two rows with the identical report start time. Neither is
            // newer than the other, so both survive this and the tie is broken below.
            .Where(s => !db.GearSnapshots.Any(o => o.CharacterId == s.CharacterId && o.RecordedAt > s.RecordedAt))
            .ToListAsync();

        return recent
            .OrderBy(s => s.CharacterId)
            .ThenByDescending(s => s.RecordedAt)
            // Most recently imported wins, then the report code for a total order — so
            // the same snapshot is chosen on every request rather than whichever the
            // database happened to return first.
            .ThenByDescending(s => s.ImportedAt)
            .ThenByDescending(s => s.WclReportCode, StringComparer.Ordinal)
            .DistinctBy(s => s.CharacterId)
            .ToList();
    }

    /// <summary>
    /// Who is wearing the item now: each character's most recent gear snapshot only.
    /// Items live in a jsonb column as an opaque string, so the match runs in C# —
    /// at guild scale that is a few dozen snapshots of ~19 items each, far cheaper
    /// than teaching EF to reach inside the document. Also returns the matched gear
    /// entry, which is where the item's name, icon and quality come from.
    /// </summary>
    private static async Task<(List<object> Equipped, JsonElement? Item)> Equipped(AppDbContext db, long itemId)
    {
        var snapshots = await LatestSnapshots(db);

        var rows = new List<(string Name, object Row)>();
        JsonElement? found = null;

        foreach (var s in snapshots)
        {
            foreach (var entry in ParseItems(s.Items).EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                // A gem is an item too — the character sheet links its sockets here,
                // so a gem id matches the piece it is socketed into.
                var worn = Num(entry, "id") == itemId;
                var socketed = !worn && Gems(entry).Contains(itemId);
                if (!worn && !socketed) continue;

                if (worn) found ??= entry;
                var c = s.Character!;
                rows.Add((c.Name, new
                {
                    characterId = c.Id,
                    name = c.Name,
                    cls = c.Class,
                    isMain = c.IsMain,
                    member = c.Member is null ? null : c.Member.Nickname ?? c.Member.DisplayName ?? c.Member.DiscordUsername,
                    slot = Str(entry, "slot"),
                    // True when it is socketed into the slot rather than worn in it.
                    asGem = socketed,
                    ilvl = worn ? Num(entry, "ilvl") : null,
                    enchantName = worn ? Str(entry, "enchantName") : null,
                    reportCode = s.WclReportCode,
                    recordedAt = s.RecordedAt
                }));
                // One row per snapshot, even if the night recorded a swap — and
                // LatestSnapshots gives one snapshot per character, so one row each.
                break;
            }
        }

        return (rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Select(r => r.Row).ToList(), found);
    }

    private static JsonElement ParseItems(string json)
    {
        try
        {
            var root = JsonDocument.Parse(json).RootElement.Clone();
            return root.ValueKind == JsonValueKind.Array ? root : Empty();
        }
        catch (JsonException) { return Empty(); }
    }

    private static JsonElement Empty() => JsonDocument.Parse("[]").RootElement.Clone();

    private static string? Str(JsonElement? el, string prop) =>
        el is { } e && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static double? Num(JsonElement? el, string prop) =>
        el is { } e && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble() : null;

    private static List<long> Gems(JsonElement el)
    {
        var gems = new List<long>();
        if (el.TryGetProperty("gems", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var g in arr.EnumerateArray())
                if (g.ValueKind == JsonValueKind.Number && g.TryGetInt64(out var v)) gems.Add(v);
        return gems;
    }

    // Warcraft Logs reports icons either bare ("inv_helmet_23") or with the file
    // extension; the page builds a zamimg URL, which wants the bare name.
    private static string? Icon(string? icon) =>
        string.IsNullOrWhiteSpace(icon) ? null
            : icon.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ? icon[..^4] : icon;

    private static IResult DbUnavailable() =>
        Results.Json(new { error = "unavailable", detail = "Persistence is not configured." }, statusCode: 503);
}
