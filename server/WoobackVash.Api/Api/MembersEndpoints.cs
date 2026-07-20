using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using WoobackVash.Api.Auth;
using WoobackVash.Api.Data;
using WoobackVash.Api.Models;

namespace WoobackVash.Api.Api;

/// <summary>
/// Identity links (Phase 3): the Discord ↔ WoW main ↔ alts mapping. Members are
/// created at login (Phase 1); here officers read them and manage the characters
/// attached to each. Officer-gated.
/// </summary>
public static class MembersEndpoints
{
    public record CharacterInput(
        Guid? MemberId,
        string Name,
        [property: JsonPropertyName("class")] string? Class,
        string? Spec,
        string? Realm,
        bool? IsMain,
        string? Notes);

    public static void MapMembersEndpoints(this IEndpointRouteBuilder app)
    {
        // All members with their linked characters.
        app.MapGet("/api/members", async (HttpContext ctx, SessionTokenService tokens) =>
        {
            var (_, error) = ctx.RequireOfficer(tokens);
            if (error is not null) return error;
            var db = ctx.RequestServices.GetService<AppDbContext>();
            if (db is null) return DbUnavailable();

            var members = await db.Members.AsNoTracking()
                .OrderBy(m => m.DisplayName)
                .Select(m => new
                {
                    id = m.Id,
                    discordUserId = m.DiscordUserId,
                    discordUsername = m.DiscordUsername,
                    displayName = m.DisplayName,
                    lastSeenAt = m.LastSeenAt,
                    characters = m.Characters
                        .OrderByDescending(c => c.IsMain).ThenBy(c => c.Name)
                        .Select(c => new { id = c.Id, name = c.Name, cls = c.Class, spec = c.Spec, realm = c.Realm, isMain = c.IsMain, notes = c.Notes })
                })
                .ToListAsync();
            return Results.Json(members);
        });

        var chars = app.MapGroup("/api/characters");

        // Characters not yet linked to a member. Loot/attendance auto-creates these
        // by name (memberId = null); officers claim them onto a member from the UI.
        // Pass ?linked=false (default) for orphans; ?linked=true lists claimed ones.
        chars.MapGet("", async (HttpContext ctx, SessionTokenService tokens) =>
        {
            var (_, error) = ctx.RequireOfficer(tokens);
            if (error is not null) return error;
            var db = ctx.RequestServices.GetService<AppDbContext>();
            if (db is null) return DbUnavailable();

            var linkedRaw = ctx.Request.Query["linked"].ToString();
            var linked = linkedRaw.Equals("true", StringComparison.OrdinalIgnoreCase);

            var q = db.Characters.AsNoTracking()
                .Where(c => linked ? c.MemberId != null : c.MemberId == null)
                .OrderBy(c => c.Name)
                .Select(c => new { id = c.Id, memberId = c.MemberId, name = c.Name, cls = c.Class, spec = c.Spec, realm = c.Realm, isMain = c.IsMain, notes = c.Notes });
            return Results.Json(await q.ToListAsync());
        });

        chars.MapPost("", async (HttpContext ctx, SessionTokenService tokens, CharacterInput input) =>
        {
            var (_, error) = ctx.RequireOfficer(tokens);
            if (error is not null) return error;
            var db = ctx.RequestServices.GetService<AppDbContext>();
            if (db is null) return DbUnavailable();

            if (string.IsNullOrWhiteSpace(input.Name))
                return Results.Json(new { error = "bad_request", detail = "Name is required." }, statusCode: 400);
            if (input.MemberId is Guid mid && !await db.Members.AnyAsync(m => m.Id == mid))
                return Results.Json(new { error = "bad_request", detail = "Unknown memberId." }, statusCode: 400);

            var c = new Character
            {
                MemberId = input.MemberId,
                Name = input.Name.Trim(),
                Class = input.Class,
                Spec = input.Spec,
                Realm = input.Realm,
                IsMain = input.IsMain ?? false,
                Notes = input.Notes
            };
            db.Characters.Add(c);
            if (c.IsMain && c.MemberId is Guid m1)
                await DemoteOtherMains(db, m1, c.Id);
            await db.SaveChangesAsync();
            return Results.Json(new { id = c.Id }, statusCode: 201);
        });

        chars.MapPut("/{id:guid}", async (HttpContext ctx, SessionTokenService tokens, Guid id, CharacterInput input) =>
        {
            var (_, error) = ctx.RequireOfficer(tokens);
            if (error is not null) return error;
            var db = ctx.RequestServices.GetService<AppDbContext>();
            if (db is null) return DbUnavailable();

            var c = await db.Characters.FirstOrDefaultAsync(x => x.Id == id);
            if (c is null) return Results.NotFound(new { error = "not_found" });
            if (input.MemberId is Guid mid && !await db.Members.AnyAsync(m => m.Id == mid))
                return Results.Json(new { error = "bad_request", detail = "Unknown memberId." }, statusCode: 400);

            if (!string.IsNullOrWhiteSpace(input.Name)) c.Name = input.Name.Trim();
            c.Class = input.Class;
            c.Spec = input.Spec;
            c.Realm = input.Realm;
            c.Notes = input.Notes;
            c.MemberId = input.MemberId;
            if (input.IsMain is bool im) c.IsMain = im;

            if (c.IsMain && c.MemberId is Guid m2)
                await DemoteOtherMains(db, m2, c.Id);
            await db.SaveChangesAsync();
            return Results.Json(new { ok = true });
        });

        chars.MapDelete("/{id:guid}", async (HttpContext ctx, SessionTokenService tokens, Guid id) =>
        {
            var (_, error) = ctx.RequireOfficer(tokens);
            if (error is not null) return error;
            var db = ctx.RequestServices.GetService<AppDbContext>();
            if (db is null) return DbUnavailable();

            var c = await db.Characters.FirstOrDefaultAsync(x => x.Id == id);
            if (c is null) return Results.NotFound(new { error = "not_found" });
            db.Characters.Remove(c);
            await db.SaveChangesAsync();
            return Results.Json(new { ok = true });
        });
    }

    // A member has at most one main; setting one demotes the others.
    private static async Task DemoteOtherMains(AppDbContext db, Guid memberId, Guid keepId)
    {
        var others = await db.Characters
            .Where(x => x.MemberId == memberId && x.IsMain && x.Id != keepId)
            .ToListAsync();
        foreach (var o in others) o.IsMain = false;
    }

    private static IResult DbUnavailable() =>
        Results.Json(new { error = "unavailable", detail = "Persistence is not configured." }, statusCode: 503);
}
