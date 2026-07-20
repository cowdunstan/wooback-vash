using Microsoft.EntityFrameworkCore;
using WoobackVash.Api.Auth;
using WoobackVash.Api.Data;
using WoobackVash.Api.Models;

namespace WoobackVash.Api.Api;

/// <summary>
/// Loot and attendance history (Phase 4), hung off a RaidEvent. Both are keyed by
/// the same event key the board uses (a Raid-Helper event id, or "default"), and
/// reference characters by name — resolved find-or-create — so records flow
/// straight from the board roster without needing characters pre-linked. Officers
/// can link those characters to members later via the identity endpoints.
/// </summary>
public static class RaidLogEndpoints
{
    public record LootInput(string Event, string Character, string ItemName, long? ItemId, string? Note, string? Title);
    public record AttendanceInput(string Event, string Character, string Status, string? Note, string? Title);

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

        attendance.MapGet("", async (HttpContext ctx, SessionTokenService tokens) =>
        {
            var (_, error) = ctx.RequireOfficer(tokens);
            if (error is not null) return error;
            var db = ctx.RequestServices.GetService<AppDbContext>();
            if (db is null) return DbUnavailable();

            var key = EventKey(ctx.Request.Query["event"]);
            var rows = await db.Attendance.AsNoTracking()
                .Where(a => a.RaidEvent!.RhEventId == key)
                .OrderBy(a => a.Character!.Name)
                .Select(a => new { id = a.Id, character = a.Character!.Name, status = a.Status.ToString(), note = a.Note, recordedAt = a.RecordedAt })
                .ToListAsync();
            return Results.Json(rows);
        });

        // Upsert one record per (event, character).
        attendance.MapPost("", async (HttpContext ctx, SessionTokenService tokens, AttendanceInput input) =>
        {
            var (_, error) = ctx.RequireOfficer(tokens);
            if (error is not null) return error;
            var db = ctx.RequestServices.GetService<AppDbContext>();
            if (db is null) return DbUnavailable();
            if (string.IsNullOrWhiteSpace(input.Character))
                return BadRequest("Character is required.");
            if (!Enum.TryParse<AttendanceStatus>(input.Status, ignoreCase: true, out var status))
                return BadRequest("Status must be one of: present, late, bench, absent.");

            var ev = await ResolveEvent(db, input.Event, input.Title);
            var ch = await ResolveCharacter(db, input.Character);
            // ev/ch may be unsaved (new) — save so their ids exist before we query.
            await db.SaveChangesAsync();

            var rec = await db.Attendance.FirstOrDefaultAsync(a => a.RaidEventId == ev.Id && a.CharacterId == ch.Id);
            if (rec is null)
            {
                rec = new AttendanceRecord { RaidEventId = ev.Id, CharacterId = ch.Id };
                db.Attendance.Add(rec);
            }
            rec.Status = status;
            rec.Note = input.Note;
            rec.RecordedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return Results.Json(new { id = rec.Id, status = rec.Status.ToString() });
        });
    }

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
