using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WoobackVash.Api.Auth;
using WoobackVash.Api.Data;
using WoobackVash.Api.Models;
using WoobackVash.Api.Services;

namespace WoobackVash.Api.Api;

/// <summary>
/// The character sheet (character.html): one character's raid setup, the gear it
/// last raided in (from the Warcraft Logs snapshots the attendance import writes),
/// everything it has won, every roll it has made, and its attendance.
///
/// Session-gated, not officer-gated — the sheet is readable by any signed-in tier,
/// like the loot history and stats pages. Ignored characters are excluded from the
/// alt switcher exactly as they are on the roster, but a direct link to one still
/// resolves, so officers can look at what they ignored.
/// </summary>
public static class CharacterSheetEndpoints
{
    /// <summary>How many past raids the sheet lists under attendance.</summary>
    private const int RecentRaids = 10;

    public static void MapCharacterSheetEndpoints(this IEndpointRouteBuilder app)
    {
        var sheet = app.MapGroup("/api/characters/sheet");

        // ?id=<guid> | ?name=<character> | nothing → the caller's own main.
        sheet.MapGet("", async (HttpContext ctx, SessionTokenService tokens) =>
        {
            var (session, error) = ctx.RequireSession(tokens);
            if (error is not null) return error;
            var db = ctx.RequestServices.GetService<AppDbContext>();
            if (db is null) return DbUnavailable();

            var (character, resolveError) = await ResolveCharacter(db, session!, ctx.Request.Query);
            if (resolveError is not null) return resolveError;
            var c = character!;

            // Everyone on the same member, minus this one — the alt switcher. An
            // unclaimed character has no member, and the null check keeps every other
            // orphan out of its switcher.
            var mid = c.MemberId;
            var alts = await db.Characters.AsNoTracking()
                .Where(x => mid != null && x.MemberId == mid && x.Id != c.Id && !x.Ignored)
                .OrderByDescending(x => x.IsMain).ThenBy(x => x.Name)
                .Select(x => new { id = x.Id, name = x.Name, cls = x.Class, isMain = x.IsMain })
                .ToListAsync();

            var member = await db.Members.AsNoTracking()
                .Where(m => mid != null && m.Id == mid)
                .Select(m => new { id = m.Id, name = m.Nickname ?? m.DisplayName ?? m.DiscordUsername })
                .FirstOrDefaultAsync();

            var gear = await LatestGear(db, c.Id);
            var history = await GearHistory(db, c.Id);
            // Whether to offer the "refresh gear" button — the endpoint enforces the same.
            var canRefreshGear = await CanRefreshGear(db, session!, c);

            // Awards won by this character, newest first, with the roll that took it.
            var loot = await db.LootAwards.AsNoTracking()
                .Where(l => l.CharacterId == c.Id)
                .OrderByDescending(l => l.AwardedAt)
                .Select(l => new
                {
                    id = l.Id,
                    itemName = l.ItemName,
                    itemId = l.ItemId,
                    awardedAt = l.AwardedAt,
                    awardedBy = l.AwardedBy,
                    note = l.Note,
                    offSpec = l.OffSpec,
                    softReserve = l.SoftReserve,
                    tmb = l.Tmb,
                    wishlist = l.Wishlist,
                    raid = l.RaidEvent != null ? l.RaidEvent.Title : null,
                    // Their own roll on the item they won, when the award carries rolls.
                    roll = l.Rolls.Where(r => r.CharacterId == c.Id)
                        .Select(r => (int?)r.Amount).FirstOrDefault(),
                    contested = l.Rolls.Count
                })
                .ToListAsync();

            // Every roll they have made, with what happened to the item.
            var rolls = await db.LootRolls.AsNoTracking()
                .Where(r => r.CharacterId == c.Id)
                .OrderByDescending(r => r.RolledAt)
                .Select(r => new
                {
                    id = r.Id,
                    itemName = r.LootAward!.ItemName,
                    itemId = r.LootAward!.ItemId,
                    amount = r.Amount,
                    classification = r.Classification,
                    rolledAt = r.RolledAt,
                    won = r.LootAward!.CharacterId == c.Id,
                    // Who took it instead (null on a win, or when it was disenchanted).
                    lostTo = r.LootAward!.CharacterId == c.Id || r.LootAward!.Character == null
                        ? null
                        : r.LootAward!.Character!.Name,
                    lostToId = r.LootAward!.CharacterId == c.Id ? null : r.LootAward!.CharacterId,
                    // Class of whoever took it, so the sheet can colour their name.
                    lostToClass = r.LootAward!.CharacterId == c.Id || r.LootAward!.Character == null
                        ? null
                        : r.LootAward!.Character!.Class,
                    disenchanted = r.LootAward!.Disenchanted
                })
                .ToListAsync();

            var wins = rolls.Count(r => r.won);
            var summary = new
            {
                rolls = rolls.Count,
                wins,
                winRate = rolls.Count == 0 ? (double?)null : Math.Round(100.0 * wins / rolls.Count, 1),
                average = rolls.Count == 0 ? (double?)null : Math.Round(rolls.Average(r => r.amount), 1),
                awards = loot.Count
            };

            var raids = await db.Attendance.AsNoTracking()
                .Where(a => a.CharacterId == c.Id && a.RaidEvent != null)
                .OrderByDescending(a => a.RaidEvent!.StartsAt)
                .Select(a => new
                {
                    code = a.RaidEvent!.WclReportCode,
                    title = a.RaidEvent!.Title,
                    startsAt = a.RaidEvent!.StartsAt,
                    status = a.Status.ToString()
                })
                .Take(RecentRaids)
                .ToListAsync();
            var raidCount = await db.Attendance.CountAsync(a => a.CharacterId == c.Id);

            return Results.Json(new
            {
                character = new
                {
                    id = c.Id,
                    name = c.Name,
                    cls = c.Class,
                    spec = c.Spec,
                    role = c.Role,
                    realm = c.Realm,
                    isMain = c.IsMain,
                    ignored = c.Ignored,
                    notes = c.Notes,
                    guildName = c.GuildName,
                    guildRank = c.GuildRank,
                    guildSyncedAt = c.GuildSyncedAt,
                    setupUpdatedAt = c.SetupUpdatedAt,
                    member,
                    alts
                },
                gear,
                gearHistory = history,
                canRefreshGear,
                loot,
                rolls,
                summary,
                attendance = new { raids = raidCount, recent = raids }
            });
        });

        // One older snapshot, for the "previous nights" picker.
        sheet.MapGet("/history", async (HttpContext ctx, SessionTokenService tokens) =>
        {
            var (session, error) = ctx.RequireSession(tokens);
            if (error is not null) return error;
            var db = ctx.RequestServices.GetService<AppDbContext>();
            if (db is null) return DbUnavailable();

            var (character, resolveError) = await ResolveCharacter(db, session!, ctx.Request.Query);
            if (resolveError is not null) return resolveError;

            var code = ctx.Request.Query["code"].ToString().Trim();
            if (string.IsNullOrEmpty(code))
                return Results.Json(new { error = "bad_request", detail = "A report code is required." }, statusCode: 400);

            var snap = await Snapshots(db, character!.Id)
                .Include(s => s.RaidEvent)
                .Where(s => s.WclReportCode == code)
                .FirstOrDefaultAsync();
            return snap is null ? Results.NotFound(new { error = "not_found" }) : Results.Json(Shape(snap));
        });

        // Refresh a character's gear on demand, so it isn't frozen at the last night
        // they raided with us. Blizzard's live equipment first (the freshest source),
        // falling back to the character's most recent Warcraft Logs report anywhere.
        // Owner-or-officer: a raider can refresh their own characters, officers anyone's.
        // On success the client re-fetches the sheet, so nothing is shaped here.
        sheet.MapPost("/refresh", async (
            HttpContext ctx, SessionTokenService tokens, BlizzardService blizzard, WarcraftLogsService wcl) =>
        {
            var (session, error) = ctx.RequireSession(tokens);
            if (error is not null) return error;
            var db = ctx.RequestServices.GetService<AppDbContext>();
            if (db is null) return DbUnavailable();

            var (character, resolveError) = await ResolveCharacter(db, session!, ctx.Request.Query);
            if (resolveError is not null) return resolveError;

            if (!await CanRefreshGear(db, session!, character!))
                return Results.Json(new { error = "forbidden", detail = "You can only refresh gear for your own characters." },
                    statusCode: 403);

            // ResolveCharacter reads AsNoTracking; reload tracked so the upsert (and any
            // setup refresh on the WCL path) is persisted.
            var ch = await db.Characters.FirstOrDefaultAsync(x => x.Id == character!.Id);
            if (ch is null) return NotFound("No such character.");

            // 1) Blizzard live gear — whatever they're wearing right now, no raid needed.
            var (bStatus, bGear, bErr) = await blizzard.GetCharacterEquipmentAsync(ch.Name);
            if (bStatus == 200 && bGear is not null && bGear.Items.Count > 0)
            {
                await GearSnapshotStore.UpsertAsync(db, ch, "blizzard", "blizzard", null,
                    DateTimeOffset.UtcNow, bGear, refreshSetup: false);
                await db.SaveChangesAsync();
                return Results.Json(new { source = "blizzard", note = "Refreshed live gear from Blizzard." });
            }

            // 2) Fall back to the character's most recent Warcraft Logs report (a pug or
            // another guild's night counts — still fresher than our last import of them).
            var (_, report, rErr) = await wcl.GetLatestReportCodeForCharacterAsync(ch.Name);
            if (report is not null)
            {
                var (_, players, _) = await wcl.GetReportGearAsync(report.Value.Code);
                var p = players?.FirstOrDefault(x => string.Equals(x.Name, ch.Name, StringComparison.OrdinalIgnoreCase));
                if (p is not null && p.Items.Count > 0)
                {
                    // Link the snapshot to a raid we already know, when the report is one.
                    var evId = await db.RaidEvents.Where(e => e.WclReportCode == report.Value.Code)
                        .Select(e => (Guid?)e.Id).FirstOrDefaultAsync();
                    await GearSnapshotStore.UpsertAsync(db, ch, "wcl", report.Value.Code, evId,
                        report.Value.StartsAt, p, refreshSetup: true);
                    await db.SaveChangesAsync();
                    return Results.Json(new { source = "wcl",
                        note = "Blizzard had nothing usable — pulled the latest Warcraft Logs report instead." });
                }
            }

            // Neither source had gear. Surface the more actionable of the two reasons.
            var detail = bErr ?? rErr ?? "No gear could be fetched from Blizzard or Warcraft Logs.";
            return Results.Json(new { error = "upstream", detail }, statusCode: 502);
        });
    }

    /// <summary>Owner-or-officer: an officer may refresh anyone's gear, a raider only
    /// the characters linked to their own Discord account. The API is the enforcement —
    /// the sheet only uses the same answer to decide whether to show the button.</summary>
    private static async Task<bool> CanRefreshGear(AppDbContext db, SessionPayload session, Character c)
    {
        if (session.Officer) return true;
        if (c.MemberId is null) return false;
        var ownerUid = await db.Members.AsNoTracking()
            .Where(m => m.Id == c.MemberId)
            .Select(m => m.DiscordUserId)
            .FirstOrDefaultAsync();
        return ownerUid is not null && ownerUid == session.Uid;
    }

    // ?id → ?name → the caller's own main (their only character when none is flagged).
    private static async Task<(Character? Character, IResult? Error)> ResolveCharacter(
        AppDbContext db, SessionPayload session, IQueryCollection query)
    {
        var idRaw = query["id"].ToString().Trim();
        if (Guid.TryParse(idRaw, out var id))
        {
            var byId = await db.Characters.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
            return byId is null ? (null, NotFound("No such character.")) : (byId, null);
        }

        var name = query["name"].ToString().Trim();
        if (!string.IsNullOrEmpty(name))
        {
            var lower = name.ToLower();
            var byName = await db.Characters.AsNoTracking().FirstOrDefaultAsync(c => c.Name.ToLower() == lower);
            return byName is null ? (null, NotFound($"No character called \"{name}\".")) : (byName, null);
        }

        var self = await db.Members.AsNoTracking().FirstOrDefaultAsync(m => m.DiscordUserId == session.Uid);
        if (self is null)
            return (null, NotFound("No member record for your account yet."));

        var mine = await db.Characters.AsNoTracking()
            .Where(c => c.MemberId == self.Id && !c.Ignored)
            .OrderByDescending(c => c.IsMain).ThenBy(c => c.Name)
            .FirstOrDefaultAsync();
        return mine is null
            ? (null, Results.Json(new
            {
                error = "no_character",
                detail = "No characters are linked to your Discord account yet — ask an officer to claim one on the roster."
            }, statusCode: 404))
            : (mine, null);
    }

    private static IQueryable<CharacterGearSnapshot> Snapshots(AppDbContext db, Guid characterId) =>
        db.GearSnapshots.AsNoTracking()
            .Where(s => s.CharacterId == characterId)
            // Two reports over one night share a start time, so the import time breaks
            // the tie — the same snapshot every request, matching the item pages.
            .OrderByDescending(s => s.RecordedAt)
            .ThenByDescending(s => s.ImportedAt);

    private static async Task<object?> LatestGear(AppDbContext db, Guid characterId)
    {
        var snap = await Snapshots(db, characterId).Include(s => s.RaidEvent).FirstOrDefaultAsync();
        return snap is null ? null : Shape(snap);
    }

    private static async Task<object> GearHistory(AppDbContext db, Guid characterId) =>
        await Snapshots(db, characterId)
            .Select(s => new
            {
                reportCode = s.WclReportCode,
                recordedAt = s.RecordedAt,
                source = s.Source,
                title = s.RaidEvent != null ? s.RaidEvent.Title : null,
                itemLevel = s.ItemLevel
            })
            .ToListAsync();

    // Items are stored as jsonb; hand them to the browser as JSON, not as a string.
    private static object Shape(CharacterGearSnapshot s) => new
    {
        reportCode = s.WclReportCode,
        recordedAt = s.RecordedAt,
        importedAt = s.ImportedAt,
        source = s.Source,
        spec = s.Spec,
        itemLevel = s.ItemLevel,
        title = s.RaidEvent?.Title,
        items = ParseItems(s.Items)
    };

    private static JsonElement ParseItems(string json)
    {
        try { return JsonDocument.Parse(json).RootElement.Clone(); }
        catch (JsonException) { return JsonDocument.Parse("[]").RootElement.Clone(); }
    }

    private static IResult NotFound(string detail) =>
        Results.Json(new { error = "not_found", detail }, statusCode: 404);

    private static IResult DbUnavailable() =>
        Results.Json(new { error = "unavailable", detail = "Persistence is not configured." }, statusCode: 503);
}
