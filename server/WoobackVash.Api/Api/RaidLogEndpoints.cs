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

    public static void MapRaidLogEndpoints(this IEndpointRouteBuilder app)
    {
        var loot = app.MapGroup("/api/loot");

        loot.MapGet("", async (HttpContext ctx, SessionTokenService tokens) =>
        {
            var (_, error) = ctx.RequireOfficer(tokens);
            if (error is not null) return error;
            var db = ctx.RequestServices.GetService<AppDbContext>();
            if (db is null) return DbUnavailable();

            var key = EventKey(ctx.Request.Query["event"]);
            var rows = await db.LootAwards.AsNoTracking()
                .Where(l => l.RaidEvent!.RhEventId == key)
                .OrderByDescending(l => l.AwardedAt)
                .Select(l => new { id = l.Id, character = l.Character!.Name, itemName = l.ItemName, itemId = l.ItemId, note = l.Note, awardedAt = l.AwardedAt })
                .ToListAsync();
            return Results.Json(rows);
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
