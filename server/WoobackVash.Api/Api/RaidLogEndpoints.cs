using Microsoft.EntityFrameworkCore;
using WoobackVash.Api.Auth;
using WoobackVash.Api.Data;
using WoobackVash.Api.Models;
using WoobackVash.Api.Services;

namespace WoobackVash.Api.Api;

/// <summary>
/// Loot and attendance history, hung off a RaidEvent. Loot is keyed by the board's
/// event key (a Raid-Helper event id, or "default") and references characters by
/// name — resolved find-or-create — so records flow straight from the board roster.
/// Attendance is driven entirely by Warcraft Logs: officers import a report, whose
/// guild-tagged players become present rows and, when new, unclaimed characters that
/// officers can link to members via the identity endpoints (members.html).
/// </summary>
public static class RaidLogEndpoints
{
    public record LootInput(string Event, string Character, string ItemName, long? ItemId, string? Note, string? Title);
    public record ImportInput(string Code);

    // ── Gargul export DTOs ──────────────────────────────────────────────────
    // Shapes mirror the Gargul addon's award JSON. Property names are matched
    // case-insensitively by the default JSON options, so "itemID"/"awardedTo" bind.
    public record GargulRoll(
        int Amount, string? Class, string? Classification, string? Player,
        int? Priority, int? PlusOneState, long Time);

    public record GargulAward(
        bool OS, bool SR, bool TMB, bool WL, bool PL,
        List<GargulRoll>? Rolls, string? AwardedBy, string? AwardedTo,
        string? Checksum, long? ItemID, string? ItemLink,
        long Timestamp, int WinnerClass);

    // Accepts either a bare Gargul array or a { "items": [...] } wrapper.
    public record GargulImportInput(List<GargulAward>? Items);

    public static void MapRaidLogEndpoints(this IEndpointRouteBuilder app)
    {
        var loot = app.MapGroup("/api/loot");

        // The award list, newest first, with every bid. Shared by the officer log
        // (which may filter to one event key) and the read-only history page.
        static async Task<IResult> LootRows(AppDbContext db, string? eventRaw)
        {
            var query = db.LootAwards.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(eventRaw))
            {
                var key = EventKey(eventRaw);
                query = query.Where(l => l.RaidEvent!.RhEventId == key);
            }

            var rows = await query
                .OrderByDescending(l => l.AwardedAt)
                .Select(l => new
                {
                    id = l.Id,
                    character = l.Character != null ? l.Character.Name : null,
                    characterClass = l.Character != null ? l.Character.Class : null,
                    disenchanted = l.Disenchanted,
                    itemName = l.ItemName,
                    itemId = l.ItemId,
                    note = l.Note,
                    awardedBy = l.AwardedBy,
                    awardedAt = l.AwardedAt,
                    // Gargul's off-spec flag on the award itself. The stats page counts
                    // OS wins with it; without it the winning roll's "OS" classification
                    // is the only (lossier) signal.
                    offSpec = l.OffSpec,
                    rolls = l.Rolls
                        .OrderByDescending(r => r.Amount)
                        .Select(r => new
                        {
                            player = r.Character != null ? r.Character.Name : null,
                            cls = r.Class,
                            amount = r.Amount,
                            classification = r.Classification,
                            priority = r.Priority
                        })
                })
                .ToListAsync();
            return Results.Json(rows);
        }

        loot.MapGet("", async (HttpContext ctx, SessionTokenService tokens) =>
        {
            var (_, error) = ctx.RequireOfficer(tokens);
            if (error is not null) return error;
            var db = ctx.RequestServices.GetService<AppDbContext>();
            if (db is null) return DbUnavailable();

            // event= filters to one event key (manual path); omitted → all awards.
            return await LootRows(db, ctx.Request.Query["event"].ToString());
        });

        // Read-only loot history for every signed-in member (loot-history.html).
        // Same rows as the officer log, minus the event filter and the write verbs.
        loot.MapGet("/history", async (HttpContext ctx, SessionTokenService tokens) =>
        {
            var (_, error) = ctx.RequireSession(tokens);
            if (error is not null) return error;
            var db = ctx.RequestServices.GetService<AppDbContext>();
            if (db is null) return DbUnavailable();

            return await LootRows(db, null);
        });

        loot.MapPost("", async (HttpContext ctx, SessionTokenService tokens, LootInput input) =>
        {
            var (session, error) = ctx.RequireOfficer(tokens);
            if (error is not null) return error;
            var db = ctx.RequestServices.GetService<AppDbContext>();
            if (db is null) return DbUnavailable();
            if (string.IsNullOrWhiteSpace(input.Character) || string.IsNullOrWhiteSpace(input.ItemName))
                return BadRequest("Character and itemName are required.");

            var ev = await ResolveEvent(db, input.Event, input.Title);
            var ch = await ResolveCharacter(db, input.Character);
            var editor = await db.Members.AsNoTracking().FirstOrDefaultAsync(m => m.DiscordUserId == session!.Uid);

            var award = new LootAward
            {
                RaidEvent = ev,
                Character = ch,
                ItemName = input.ItemName.Trim(),
                ItemId = input.ItemId,
                Note = input.Note,
                AwardedByMemberId = editor?.Id
            };
            db.LootAwards.Add(award);
            await db.SaveChangesAsync();
            return Results.Json(new { id = award.Id }, statusCode: 201);
        });

        loot.MapDelete("/{id:guid}", async (HttpContext ctx, SessionTokenService tokens, Guid id) =>
        {
            var (_, error) = ctx.RequireOfficer(tokens);
            if (error is not null) return error;
            var db = ctx.RequestServices.GetService<AppDbContext>();
            if (db is null) return DbUnavailable();

            var award = await db.LootAwards.FirstOrDefaultAsync(l => l.Id == id);
            if (award is null) return Results.NotFound(new { error = "not_found" });
            db.LootAwards.Remove(award);
            await db.SaveChangesAsync();
            return Results.Json(new { ok = true });
        });

        // Bulk import a Gargul export: each award becomes a standalone LootAward
        // (no raid event), disenchants are flagged, and every bid is stored as a
        // LootRoll against its bidding character. Idempotent — awards already seen
        // (matched on Gargul's checksum) are skipped, so re-importing is safe.
        loot.MapPost("/import", async (HttpContext ctx, SessionTokenService tokens, GargulImportInput input) =>
        {
            var (_, error) = ctx.RequireOfficer(tokens);
            if (error is not null) return error;
            var db = ctx.RequestServices.GetService<AppDbContext>();
            if (db is null) return DbUnavailable();

            var awards = input.Items;
            if (awards is null || awards.Count == 0)
                return BadRequest("No Gargul awards found in the payload.");

            // Which checksums do we already have? Skip those to stay idempotent.
            var incoming = awards.Where(a => !string.IsNullOrWhiteSpace(a.Checksum))
                                 .Select(a => a.Checksum!).Distinct().ToList();
            var known = await db.LootAwards
                .Where(l => l.Checksum != null && incoming.Contains(l.Checksum))
                .Select(l => l.Checksum!)
                .ToListAsync();
            var seen = new HashSet<string>(known);

            int imported = 0, skipped = 0, disenchants = 0, rollsRecorded = 0;
            var newCharacters = 0;
            // Cache resolved characters within this import so repeat bidders/winners
            // don't each hit the DB (and to avoid double-adding a brand-new one).
            var charCache = new Dictionary<string, Character>(StringComparer.OrdinalIgnoreCase);

            foreach (var a in awards)
            {
                // De-dupe both against the DB and within this payload.
                if (!string.IsNullOrWhiteSpace(a.Checksum) && !seen.Add(a.Checksum!))
                {
                    skipped++;
                    continue;
                }

                var isDe = string.Equals(a.AwardedTo, "|de|", StringComparison.OrdinalIgnoreCase);
                Character? winner = null;
                if (!isDe && !string.IsNullOrWhiteSpace(a.AwardedTo))
                {
                    var (name, realm) = SplitNameRealm(a.AwardedTo!);
                    (winner, var wasNew) = await ResolveBidder(db, charCache, name, realm, ClassName(a.WinnerClass));
                    if (wasNew) newCharacters++;
                }
                else if (isDe)
                {
                    disenchants++;
                }

                var award = new LootAward
                {
                    Character = winner,
                    ItemName = StripItemLink(a.ItemLink) ?? "Unknown item",
                    ItemId = a.ItemID,
                    AwardedBy = a.AwardedBy,
                    Checksum = string.IsNullOrWhiteSpace(a.Checksum) ? null : a.Checksum,
                    Disenchanted = isDe,
                    WinnerClass = a.WinnerClass == 0 ? null : a.WinnerClass,
                    OffSpec = a.OS,
                    SoftReserve = a.SR,
                    Tmb = a.TMB,
                    Wishlist = a.WL,
                    PlusOne = a.PL,
                    AwardedAt = a.Timestamp > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(a.Timestamp)
                        : DateTimeOffset.UtcNow
                };

                foreach (var r in a.Rolls ?? Enumerable.Empty<GargulRoll>())
                {
                    if (string.IsNullOrWhiteSpace(r.Player)) continue;
                    var (bidder, bidderNew) = await ResolveBidder(db, charCache, r.Player!, null, r.Class);
                    if (bidderNew) newCharacters++;
                    award.Rolls.Add(new LootRoll
                    {
                        Character = bidder,
                        Amount = r.Amount,
                        Classification = NullIfBlank(r.Classification),
                        Priority = r.Priority,
                        PlusOneState = r.PlusOneState,
                        Class = NullIfBlank(r.Class),
                        RolledAt = r.Time > 0
                            ? DateTimeOffset.FromUnixTimeSeconds(r.Time)
                            : award.AwardedAt
                    });
                    rollsRecorded++;
                }

                db.LootAwards.Add(award);
                imported++;
            }

            await db.SaveChangesAsync();
            return Results.Json(new { imported, skipped, disenchants, rollsRecorded, newCharacters });
        });

        var attendance = app.MapGroup("/api/attendance");

        // Raid nights with imported attendance, newest first — drives the picker.
        attendance.MapGet("/events", async (HttpContext ctx, SessionTokenService tokens) =>
        {
            var (_, error) = ctx.RequireOfficer(tokens);
            if (error is not null) return error;
            var db = ctx.RequestServices.GetService<AppDbContext>();
            if (db is null) return DbUnavailable();

            var events = await db.RaidEvents.AsNoTracking()
                .Where(e => e.WclReportCode != null)
                .OrderByDescending(e => e.StartsAt)
                .Select(e => new { code = e.WclReportCode, title = e.Title, startsAt = e.StartsAt, count = e.Attendance.Count })
                .ToListAsync();
            return Results.Json(events);
        });

        // Attendance rows for one imported report, each with its claim status.
        attendance.MapGet("", async (HttpContext ctx, SessionTokenService tokens) =>
        {
            var (_, error) = ctx.RequireOfficer(tokens);
            if (error is not null) return error;
            var db = ctx.RequestServices.GetService<AppDbContext>();
            if (db is null) return DbUnavailable();

            var code = ctx.Request.Query["code"].ToString().Trim();
            if (string.IsNullOrEmpty(code)) return BadRequest("A report code is required.");

            var rows = await db.Attendance.AsNoTracking()
                .Where(a => a.RaidEvent!.WclReportCode == code)
                .OrderBy(a => a.Character!.Name)
                .Select(a => new
                {
                    id = a.Id,
                    character = a.Character!.Name,
                    cls = a.Character!.Class,
                    realm = a.Character!.Realm,
                    status = a.Status.ToString(),
                    memberId = a.Character!.MemberId,
                    member = a.Character!.Member != null
                        ? (a.Character.Member.DisplayName ?? a.Character.Member.DiscordUsername)
                        : null
                })
                .ToListAsync();
            return Results.Json(rows);
        });

        // Import a Warcraft Logs report: its guild-tagged players become present
        // attendance rows; players we've never seen become unclaimed characters.
        attendance.MapPost("/import", async (HttpContext ctx, SessionTokenService tokens, WarcraftLogsService wcl, ImportInput input) =>
        {
            var (_, error) = ctx.RequireOfficer(tokens);
            if (error is not null) return error;
            var db = ctx.RequestServices.GetService<AppDbContext>();
            if (db is null) return DbUnavailable();
            if (string.IsNullOrWhiteSpace(input.Code)) return BadRequest("A report code is required.");

            var (status, result, wclError) = await wcl.GetReportPlayersAsync(input.Code.Trim());
            if (result is null)
                return Results.Json(new { error = "wcl", detail = wclError ?? "Warcraft Logs pull failed." }, statusCode: status);

            var ev = await ResolveWclEvent(db, result);

            var newCharacters = 0;
            var characters = new List<Character>(result.Players.Count);
            foreach (var p in result.Players)
            {
                var (ch, wasNew) = await ResolveCharacterFromLog(db, p.Name, p.Realm, p.Cls);
                if (wasNew) newCharacters++;
                characters.Add(ch);
            }
            // Save so event + character ids exist before we upsert attendance.
            await db.SaveChangesAsync();

            var existing = await db.Attendance
                .Where(a => a.RaidEventId == ev.Id)
                .ToDictionaryAsync(a => a.CharacterId, a => a);
            foreach (var ch in characters)
            {
                if (!existing.TryGetValue(ch.Id, out var rec))
                {
                    rec = new AttendanceRecord { RaidEventId = ev.Id, CharacterId = ch.Id };
                    db.Attendance.Add(rec);
                }
                rec.Status = AttendanceStatus.Present;
                rec.RecordedAt = DateTimeOffset.UtcNow;
            }
            await db.SaveChangesAsync();

            return Results.Json(new
            {
                code = result.Code,
                title = ev.Title,
                imported = characters.Count,
                newCharacters,
                filteredOut = result.FilteredOut
            });
        });
    }

    // Find-or-create the RaidEvent for a WCL report, filling meta from the report.
    private static async Task<RaidEvent> ResolveWclEvent(AppDbContext db, WarcraftLogsService.ReportPlayers r)
    {
        var ev = await db.RaidEvents.FirstOrDefaultAsync(e => e.WclReportCode == r.Code);
        if (ev is null)
        {
            ev = new RaidEvent { WclReportCode = r.Code };
            db.RaidEvents.Add(ev);
        }
        ev.Title ??= r.Title;
        ev.Zone ??= r.Zone;
        ev.StartsAt ??= r.StartTime > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(r.StartTime) : null;
        return ev;
    }

    // Like ResolveCharacter, but for log imports: seeds class/realm on creation and
    // backfills them on an existing character when we have data and it doesn't.
    private static async Task<(Character Character, bool WasNew)> ResolveCharacterFromLog(
        AppDbContext db, string name, string? realm, string? cls)
    {
        var n = name.Trim();
        var lower = n.ToLower();
        var c = await db.Characters.FirstOrDefaultAsync(x => x.Name.ToLower() == lower);
        if (c is null)
        {
            c = new Character { Name = n, Realm = NullIfBlank(realm), Class = NullIfBlank(cls) };
            db.Characters.Add(c);
            return (c, true);
        }
        c.Realm ??= NullIfBlank(realm);
        c.Class ??= NullIfBlank(cls);
        return (c, false);
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    // Find-or-create a character for a Gargul import (winner or bidder), caching by
    // name within the import and backfilling realm/class when we learn them.
    private static async Task<(Character Character, bool WasNew)> ResolveBidder(
        AppDbContext db, Dictionary<string, Character> cache, string name, string? realm, string? cls)
    {
        var n = name.Trim();
        if (cache.TryGetValue(n, out var cached))
        {
            cached.Realm ??= NullIfBlank(realm);
            cached.Class ??= NullIfBlank(cls);
            return (cached, false);
        }

        var lower = n.ToLower();
        var c = await db.Characters.FirstOrDefaultAsync(x => x.Name.ToLower() == lower);
        var wasNew = false;
        if (c is null)
        {
            c = new Character { Name = n, Realm = NullIfBlank(realm), Class = NullIfBlank(cls) };
            db.Characters.Add(c);
            wasNew = true;
        }
        else
        {
            c.Realm ??= NullIfBlank(realm);
            c.Class ??= NullIfBlank(cls);
        }
        cache[n] = c;
        return (c, wasNew);
    }

    // "Name-Realm" → (Name, Realm). Splits on the first hyphen; realm may be null.
    private static (string Name, string? Realm) SplitNameRealm(string awardedTo)
    {
        var s = awardedTo.Trim();
        var dash = s.IndexOf('-');
        return dash < 0 ? (s, null) : (s[..dash], NullIfBlank(s[(dash + 1)..]));
    }

    // Gargul's itemLink is a bracketed name, e.g. "[Boots of the Shifting Nightmare]".
    private static string? StripItemLink(string? link)
    {
        var s = link?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        if (s.StartsWith('[') && s.EndsWith(']') && s.Length >= 2) s = s[1..^1];
        return NullIfBlank(s);
    }

    // WoW class id → class string (matches the roster's lower-case class names).
    private static string? ClassName(int id) => id switch
    {
        1 => "warrior", 2 => "paladin", 3 => "hunter", 4 => "rogue",
        5 => "priest", 6 => "deathknight", 7 => "shaman", 8 => "mage",
        9 => "warlock", 10 => "monk", 11 => "druid", 12 => "demonhunter",
        _ => null
    };

    private static async Task<RaidEvent> ResolveEvent(AppDbContext db, string? key, string? title)
    {
        var k = EventKey(key);
        var ev = await db.RaidEvents.FirstOrDefaultAsync(e => e.RhEventId == k);
        if (ev is null)
        {
            ev = new RaidEvent { RhEventId = k, Title = title };
            db.RaidEvents.Add(ev);
        }
        else if (!string.IsNullOrEmpty(title) && string.IsNullOrEmpty(ev.Title))
        {
            ev.Title = title;
        }
        return ev;
    }

    private static async Task<Character> ResolveCharacter(AppDbContext db, string name)
    {
        var n = name.Trim();
        var lower = n.ToLower();
        var c = await db.Characters.FirstOrDefaultAsync(x => x.Name.ToLower() == lower);
        if (c is null)
        {
            c = new Character { Name = n };
            db.Characters.Add(c);
        }
        return c;
    }

    private static string EventKey(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? "default" : raw.Trim();

    private static IResult BadRequest(string detail) =>
        Results.Json(new { error = "bad_request", detail }, statusCode: 400);

    private static IResult DbUnavailable() =>
        Results.Json(new { error = "unavailable", detail = "Persistence is not configured." }, statusCode: 503);
}
