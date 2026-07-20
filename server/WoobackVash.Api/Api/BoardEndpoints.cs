using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WoobackVash.Api.Auth;
using WoobackVash.Api.Data;
using WoobackVash.Api.Models;

namespace WoobackVash.Api.Api;

/// <summary>
/// Vash board persistence (Phase 3). Officers save the whole board snapshot
/// (guild name, slot counts, roster, assignments) under a storage key — a
/// Raid-Helper event id, or "default" for a manually built board — and load it
/// back. Saved explicitly via the board's "Save layout" button. The snapshot
/// mirrors the in-browser state in app.js, so it round-trips losslessly.
/// </summary>
public static class BoardEndpoints
{
    public record SaveBoardRequest(JsonElement State, string? Title);

    public static void MapBoardEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/board");

        // Load a saved layout. 200 with { found:false } when there's nothing yet.
        group.MapGet("", async (HttpContext ctx, SessionTokenService tokens) =>
        {
            var (session, error) = ctx.RequireOfficer(tokens);
            if (error is not null) return error;

            var db = ctx.RequestServices.GetService<AppDbContext>();
            if (db is null) return DbUnavailable();

            var key = Key(ctx.Request.Query["key"]);
            var layout = await db.BoardLayouts.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Name == key);
            if (layout is null)
                return Results.Json(new { found = false, key });

            // Embed the stored jsonb verbatim rather than re-parsing it.
            var payload = "{\"found\":true," +
                          "\"key\":" + JsonSerializer.Serialize(layout.Name) + "," +
                          "\"updatedAt\":" + JsonSerializer.Serialize(layout.UpdatedAt) + "," +
                          "\"state\":" + layout.State + "}";
            return Results.Text(payload, "application/json");
        });

        // Save (upsert) a layout under its key.
        group.MapPut("", async (HttpContext ctx, SessionTokenService tokens, SaveBoardRequest req) =>
        {
            var (session, error) = ctx.RequireOfficer(tokens);
            if (error is not null) return error;

            var db = ctx.RequestServices.GetService<AppDbContext>();
            if (db is null) return DbUnavailable();

            if (req.State.ValueKind != JsonValueKind.Object)
                return Results.Json(new { error = "bad_request", detail = "State must be a JSON object." }, statusCode: 400);

            var key = Key(ctx.Request.Query["key"]);

            // Link a RaidEvent for real event keys, so loot/attendance can join later.
            Guid? raidEventId = null;
            if (key != "default")
            {
                var ev = await db.RaidEvents.FirstOrDefaultAsync(e => e.RhEventId == key);
                if (ev is null)
                {
                    ev = new RaidEvent { RhEventId = key, Title = req.Title };
                    db.RaidEvents.Add(ev);
                }
                else if (!string.IsNullOrEmpty(req.Title))
                {
                    ev.Title = req.Title;
                }
                raidEventId = ev.Id;
            }

            var editor = await db.Members.AsNoTracking()
                .FirstOrDefaultAsync(m => m.DiscordUserId == session!.Uid);

            var stateJson = JsonSerializer.Serialize(req.State);
            var layout = await db.BoardLayouts.FirstOrDefaultAsync(x => x.Name == key);
            if (layout is null)
            {
                layout = new BoardLayout { Name = key };
                db.BoardLayouts.Add(layout);
            }
            layout.State = stateJson;
            layout.RaidEventId = raidEventId;
            layout.UpdatedByMemberId = editor?.Id;
            layout.UpdatedAt = DateTimeOffset.UtcNow;

            await db.SaveChangesAsync();
            return Results.Json(new { ok = true, key, updatedAt = layout.UpdatedAt });
        });

        // List saved layouts (for a future "load previous" menu).
        group.MapGet("/list", async (HttpContext ctx, SessionTokenService tokens) =>
        {
            var (session, error) = ctx.RequireOfficer(tokens);
            if (error is not null) return error;

            var db = ctx.RequestServices.GetService<AppDbContext>();
            if (db is null) return DbUnavailable();

            var layouts = await db.BoardLayouts.AsNoTracking()
                .OrderByDescending(x => x.UpdatedAt)
                .Select(x => new { key = x.Name, updatedAt = x.UpdatedAt, hasEvent = x.RaidEventId != null })
                .ToListAsync();
            return Results.Json(layouts);
        });
    }

    private static IResult DbUnavailable() =>
        Results.Json(new { error = "unavailable", detail = "Persistence is not configured." }, statusCode: 503);

    private static string Key(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? "default" : raw.Trim();
}
