using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WoobackVash.Api.Auth;
using WoobackVash.Api.Config;
using WoobackVash.Api.Data;
using WoobackVash.Api.Models;
using WoobackVash.Api.Services;

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
        string? Notes,
        bool? Ignored);

    // Shape of a guild-member object from GET /guilds/{id}/members.
    private record DiscordUser(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("username")] string? Username,
        [property: JsonPropertyName("global_name")] string? GlobalName);

    private record GuildMember(
        [property: JsonPropertyName("roles")] string[]? Roles,
        [property: JsonPropertyName("nick")] string? Nick,
        [property: JsonPropertyName("user")] DiscordUser? User);

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
                    nickname = m.Nickname,
                    lastSeenAt = m.LastSeenAt,
                    // Ignored characters are hidden here and listed on their own at the
                    // bottom of the members page (GET /api/characters?ignored=true).
                    characters = m.Characters
                        .Where(c => !c.Ignored)
                        .OrderByDescending(c => c.IsMain).ThenBy(c => c.Name)
                        .Select(c => new
                        {
                            id = c.Id, name = c.Name, cls = c.Class, spec = c.Spec, realm = c.Realm,
                            isMain = c.IsMain, notes = c.Notes, ignored = c.Ignored,
                            guildName = c.GuildName, guildRank = c.GuildRank, guildSyncedAt = c.GuildSyncedAt
                        })
                })
                .ToListAsync();
            return Results.Json(members);
        });

        // Pull the guild's Discord roster and create an identity-link (Member) row
        // for everyone holding the member (home) role or an officer role. Lets
        // officers seed the roster from Discord instead of waiting for each raider
        // to sign in. Upsert keyed on Discord user id: creates missing members and
        // refreshes usernames on existing ones. Needs a bot token (privileged
        // GUILD_MEMBERS intent) — the per-user OAuth token can't list the guild.
        app.MapPost("/api/members/import-discord", async (
            HttpContext ctx,
            SessionTokenService tokens,
            IOptions<DiscordOptions> opt,
            IHttpClientFactory httpFactory,
            ILoggerFactory logFactory) =>
        {
            var (_, error) = ctx.RequireOfficer(tokens);
            if (error is not null) return error;
            var db = ctx.RequestServices.GetService<AppDbContext>();
            if (db is null) return DbUnavailable();

            var d = opt.Value;
            if (string.IsNullOrWhiteSpace(d.BotToken))
                return Results.Json(new { error = "not_configured", detail = "Discord bot token is not configured on the server." }, statusCode: 503);
            if (string.IsNullOrWhiteSpace(d.GuildId))
                return Results.Json(new { error = "not_configured", detail = "Discord guild id is not configured." }, statusCode: 503);

            var log = logFactory.CreateLogger("ImportDiscord");
            var http = httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(20);

            // Roles that qualify: the home ("member") role, plus any officer role.
            var qualifying = new HashSet<string>(d.OfficerRoleIds, StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(d.HomeRoleId)) qualifying.Add(d.HomeRoleId);

            // Page through the guild members (max 1000 per page, sorted by user id
            // ascending; keep going with ?after=<last id> until a short page).
            var matched = new List<GuildMember>();
            var scanned = 0;
            string? after = null;
            try
            {
                for (var page = 0; page < 50; page++) // safety cap: 50k members
                {
                    var url = $"https://discord.com/api/guilds/{d.GuildId}/members?limit=1000"
                              + (after is null ? "" : "&after=" + Uri.EscapeDataString(after));
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bot", d.BotToken);
                    var r = await http.SendAsync(req);
                    if (r.StatusCode == System.Net.HttpStatusCode.Forbidden)
                        return Results.Json(new { error = "discord_forbidden", detail = "The bot can't list members. Enable the Server Members Intent and make sure it's in the guild." }, statusCode: 502);
                    if (!r.IsSuccessStatusCode)
                        return Results.Json(new { error = "discord_http", detail = "Discord returned HTTP " + (int)r.StatusCode + "." }, statusCode: 502);

                    var batch = await r.Content.ReadFromJsonAsync<List<GuildMember>>() ?? new();
                    if (batch.Count == 0) break;
                    scanned += batch.Count;

                    foreach (var gm in batch)
                    {
                        if (gm.User?.Id is { Length: > 0 } && (gm.Roles ?? Array.Empty<string>()).Any(qualifying.Contains))
                            matched.Add(gm);
                    }

                    if (batch.Count < 1000) break;
                    after = batch[^1].User?.Id;
                    if (string.IsNullOrEmpty(after)) break;
                }
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Discord member listing failed");
                return Results.Json(new { error = "discord_unreachable", detail = "Could not reach Discord." }, statusCode: 502);
            }

            // Upsert. Load the members we might touch in one query, then create or
            // refresh. New members are linked (a Discord↔member row) with no
            // characters yet, ready for officers to attach mains/alts.
            var ids = matched.Select(m => m.User!.Id!).Distinct().ToList();
            var existing = await db.Members.Where(m => ids.Contains(m.DiscordUserId))
                .ToDictionaryAsync(m => m.DiscordUserId, m => m);

            int created = 0, updated = 0;
            foreach (var gm in matched)
            {
                var uid = gm.User!.Id!;
                var username = gm.User.Username;
                var display = gm.User.GlobalName ?? gm.User.Username ?? uid;
                var nick = string.IsNullOrWhiteSpace(gm.Nick) ? null : gm.Nick;
                if (existing.TryGetValue(uid, out var m))
                {
                    if (m.DiscordUsername != username || m.DisplayName != display || m.Nickname != nick)
                    {
                        m.DiscordUsername = username;
                        m.DisplayName = display;
                        m.Nickname = nick;
                        updated++;
                    }
                }
                else
                {
                    var row = new Member { DiscordUserId = uid, DiscordUsername = username, DisplayName = display, Nickname = nick };
                    db.Members.Add(row);
                    existing[uid] = row; // guard against duplicate ids within a page
                    created++;
                }
            }
            await db.SaveChangesAsync();

            return Results.Json(new { scanned, matched = matched.Count, created, updated });
        });

        var chars = app.MapGroup("/api/characters");

        // Characters not yet linked to a member. Loot/attendance auto-creates these
        // by name (memberId = null); officers claim them onto a member from the UI.
        // Pass ?linked=false (default) for orphans; ?linked=true lists claimed ones.
        // ?ignored=true instead returns every ignored character, linked or not — that's
        // the "Ignored characters" section at the bottom of the members page — and the
        // linked filter does not apply. Otherwise ignored characters are left out.
        chars.MapGet("", async (HttpContext ctx, SessionTokenService tokens) =>
        {
            var (_, error) = ctx.RequireOfficer(tokens);
            if (error is not null) return error;
            var db = ctx.RequestServices.GetService<AppDbContext>();
            if (db is null) return DbUnavailable();

            var linked = ctx.Request.Query["linked"].ToString().Equals("true", StringComparison.OrdinalIgnoreCase);
            var ignored = ctx.Request.Query["ignored"].ToString().Equals("true", StringComparison.OrdinalIgnoreCase);

            var q = db.Characters.AsNoTracking()
                .Where(c => ignored
                    ? c.Ignored
                    : !c.Ignored && (linked ? c.MemberId != null : c.MemberId == null))
                .OrderBy(c => c.Name)
                .Select(c => new
                {
                    id = c.Id, memberId = c.MemberId, name = c.Name, cls = c.Class, spec = c.Spec,
                    realm = c.Realm, isMain = c.IsMain, notes = c.Notes, ignored = c.Ignored,
                    guildName = c.GuildName, guildRank = c.GuildRank, guildSyncedAt = c.GuildSyncedAt,
                    // Where an ignored character came from, so the bottom section can say so.
                    member = c.Member != null
                        ? (c.Member.Nickname ?? c.Member.DisplayName ?? c.Member.DiscordUsername)
                        : null
                });
            return Results.Json(await q.ToListAsync());
        });

        // Pull the Blizzard guild roster and record, per character, whether it is
        // currently in the guild (plus its rank). A character missing from the roster
        // has its guild cleared — that's the signal officers use to decide what to
        // ignore. The sync never creates, deletes, or ignores anything by itself.
        chars.MapPost("/sync-guild", async (
            HttpContext ctx,
            SessionTokenService tokens,
            BlizzardService blizzard,
            IOptions<BlizzardOptions> opt) =>
        {
            var (_, error) = ctx.RequireOfficer(tokens);
            if (error is not null) return error;
            var db = ctx.RequestServices.GetService<AppDbContext>();
            if (db is null) return DbUnavailable();

            // Officers press this to see the roster as it is right now, so skip the TTL.
            var (status, roster, blizzErr) = await blizzard.GetGuildRosterAsync(forceRefresh: true);
            if (roster is null)
                return Results.Json(new { error = "blizzard", detail = blizzErr ?? "Blizzard roster pull failed." },
                    statusCode: status);

            // Name → roster entry. One realm, so names are the key; when a character
            // carries a realm we still require it to match the roster's slug.
            var byName = new Dictionary<string, BlizzardService.RosterMember>(StringComparer.OrdinalIgnoreCase);
            foreach (var rm in roster) byName[rm.Name] = rm;

            var guildName = opt.Value.GuildName;
            var now = DateTimeOffset.UtcNow;
            var characters = await db.Characters.ToListAsync();
            int inGuild = 0, notInGuild = 0;
            var matchedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var c in characters)
            {
                var hit = byName.TryGetValue(c.Name, out var rm) && RealmMatches(c.Realm, rm!.RealmSlug) ? rm : null;
                if (hit is not null)
                {
                    c.GuildName = guildName;
                    c.GuildRank = hit.Rank;
                    matchedNames.Add(c.Name);
                    inGuild++;
                }
                else
                {
                    c.GuildName = null;
                    c.GuildRank = null;
                    notInGuild++;
                }
                c.GuildSyncedAt = now;
            }
            await db.SaveChangesAsync();

            // Roster names we hold no character row for — reported so officers know the
            // roster is ahead of the database, but nothing is created.
            var unmatchedRoster = roster.Count(rm => !matchedNames.Contains(rm.Name));

            return Results.Json(new { rosterCount = roster.Count, inGuild, notInGuild, unmatchedRoster, syncedAt = now });
        });

        // Add a character. Officers may attach it to any member (or leave it an
        // orphan) — that's how they seed the roster and claim unlinked characters.
        // Everyone else is pinned to their own member: a non-officer can only create
        // characters for themselves, and a memberId for anyone else is rejected.
        chars.MapPost("", async (HttpContext ctx, SessionTokenService tokens, CharacterInput input) =>
        {
            var (session, error) = ctx.RequireSession(tokens);
            if (error is not null) return error;
            var db = ctx.RequestServices.GetService<AppDbContext>();
            if (db is null) return DbUnavailable();

            if (string.IsNullOrWhiteSpace(input.Name))
                return Results.Json(new { error = "bad_request", detail = "Name is required." }, statusCode: 400);

            var (memberId, mErr) = await ResolveTargetMember(db, session!, input.MemberId);
            if (mErr is not null) return mErr;

            var c = new Character
            {
                MemberId = memberId,
                Name = input.Name.Trim(),
                Class = input.Class,
                Spec = input.Spec,
                Realm = input.Realm,
                IsMain = input.IsMain ?? false,
                Notes = input.Notes,
                Ignored = input.Ignored ?? false
            };
            db.Characters.Add(c);
            if (c.IsMain && c.MemberId is Guid m1)
                await DemoteOtherMains(db, m1, c.Id);
            await db.SaveChangesAsync();
            return Results.Json(new { id = c.Id }, statusCode: 201);
        });

        // Edit a character. Officers may edit any character and reassign it to any
        // member (or unlink it); everyone else may only edit their own and may not
        // reassign it away from — or claim it onto — someone else's member.
        chars.MapPut("/{id:guid}", async (HttpContext ctx, SessionTokenService tokens, Guid id, CharacterInput input) =>
        {
            var (session, error) = ctx.RequireSession(tokens);
            if (error is not null) return error;
            var db = ctx.RequestServices.GetService<AppDbContext>();
            if (db is null) return DbUnavailable();

            var c = await db.Characters.FirstOrDefaultAsync(x => x.Id == id);
            if (c is null) return Results.NotFound(new { error = "not_found" });

            var accessErr = await AuthorizeCharacterAccess(db, session!, c);
            if (accessErr is not null) return accessErr;
            var (memberId, mErr) = await ResolveTargetMember(db, session!, input.MemberId);
            if (mErr is not null) return mErr;

            if (!string.IsNullOrWhiteSpace(input.Name)) c.Name = input.Name.Trim();
            c.Class = input.Class;
            c.Spec = input.Spec;
            c.Realm = input.Realm;
            c.Notes = input.Notes;
            c.MemberId = memberId;
            if (input.IsMain is bool im) c.IsMain = im;
            if (input.Ignored is bool ig) c.Ignored = ig;

            if (c.IsMain && c.MemberId is Guid m2)
                await DemoteOtherMains(db, m2, c.Id);
            await db.SaveChangesAsync();
            return Results.Json(new { ok = true });
        });

        // Delete a character. Officers may delete any; everyone else only their own.
        chars.MapDelete("/{id:guid}", async (HttpContext ctx, SessionTokenService tokens, Guid id) =>
        {
            var (session, error) = ctx.RequireSession(tokens);
            if (error is not null) return error;
            var db = ctx.RequestServices.GetService<AppDbContext>();
            if (db is null) return DbUnavailable();

            var c = await db.Characters.FirstOrDefaultAsync(x => x.Id == id);
            if (c is null) return Results.NotFound(new { error = "not_found" });

            var accessErr = await AuthorizeCharacterAccess(db, session!, c);
            if (accessErr is not null) return accessErr;

            db.Characters.Remove(c);
            await db.SaveChangesAsync();
            return Results.Json(new { ok = true });
        });
    }

    // A character's realm matches a roster entry when either side is blank (most rows
    // carry no realm — the guild is single-realm) or the two slugs agree.
    private static bool RealmMatches(string? charRealm, string? rosterSlug)
    {
        if (string.IsNullOrWhiteSpace(charRealm) || string.IsNullOrWhiteSpace(rosterSlug)) return true;
        return Slug(charRealm).Equals(Slug(rosterSlug), StringComparison.OrdinalIgnoreCase);
    }

    // "Dreamscythe" / "dream scythe" → "dreamscythe", matching Blizzard's realm slugs.
    private static string Slug(string s) =>
        new string(s.Where(ch => !char.IsWhiteSpace(ch) && ch != '-' && ch != '\'').ToArray());

    // Resolve which member a create/edit should attach a character to, enforcing
    // the self-only rule for non-officers. Officers may target any existing member
    // (or null — an orphan); everyone else is pinned to their own member and may
    // not target someone else's. Returns (memberId, null) or (default, error).
    private static async Task<(Guid? memberId, IResult? error)> ResolveTargetMember(
        AppDbContext db, SessionPayload session, Guid? requested)
    {
        if (session.Officer)
        {
            if (requested is Guid mid && !await db.Members.AnyAsync(m => m.Id == mid))
                return (null, Results.Json(new { error = "bad_request", detail = "Unknown memberId." }, statusCode: 400));
            return (requested, null);
        }

        var self = await db.Members.FirstOrDefaultAsync(m => m.DiscordUserId == session.Uid);
        if (self is null)
            return (null, Results.Json(new { error = "no_member", detail = "No member record for your account." }, statusCode: 404));
        if (requested is Guid rid && rid != self.Id)
            return (null, Results.Json(new { error = "forbidden", detail = "You can only assign characters to your own member." }, statusCode: 403));
        return (self.Id, null);
    }

    // Authorize a write to an existing character. Officers may modify any character;
    // everyone else only their own. Returns null when allowed, else an error.
    private static async Task<IResult?> AuthorizeCharacterAccess(
        AppDbContext db, SessionPayload session, Character c)
    {
        if (session.Officer) return null;
        var self = await db.Members.FirstOrDefaultAsync(m => m.DiscordUserId == session.Uid);
        if (self is null)
            return Results.Json(new { error = "no_member", detail = "No member record for your account." }, statusCode: 404);
        if (c.MemberId != self.Id)
            return Results.Json(new { error = "forbidden", detail = "You can only manage your own characters." }, statusCode: 403);
        return null;
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
