using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WoobackVash.Api.Config;
using WoobackVash.Api.Data;
using WoobackVash.Api.Models;

namespace WoobackVash.Api.Auth;

/// <summary>
/// Discord OAuth login, ported from raidhelper-proxy.worker.js (handleLogin /
/// handleCallback). A raider signs in with Discord; we read their guild roles and
/// grant access in two tiers (home vs officer), upsert their Member row, then mint
/// a session token and bounce back to the static site with it in the URL fragment.
/// </summary>
public static class AuthEndpoints
{
    private const string DiscordApi = "https://discord.com/api";
    private const string StateCookie = "rh_oauth_state";

    private record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken);

    private record DiscordUser(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("username")] string? Username,
        [property: JsonPropertyName("global_name")] string? GlobalName);

    private record GuildMember(
        [property: JsonPropertyName("roles")] string[]? Roles,
        [property: JsonPropertyName("nick")] string? Nick,
        [property: JsonPropertyName("user")] DiscordUser? User);

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/auth/login", (HttpContext ctx, IOptions<DiscordOptions> opt) =>
        {
            var d = opt.Value;
            var state = Base64Url(RandomNumberGenerator.GetBytes(16));

            ctx.Response.Cookies.Append(StateCookie, state, new CookieOptions
            {
                HttpOnly = true,
                Secure = ctx.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                Path = "/",
                MaxAge = TimeSpan.FromSeconds(600)
            });

            var query = string.Join('&', new[]
            {
                "client_id=" + Uri.EscapeDataString(d.ClientId),
                "redirect_uri=" + Uri.EscapeDataString(d.RedirectUri),
                "response_type=code",
                "scope=" + Uri.EscapeDataString(d.Scopes),
                "state=" + Uri.EscapeDataString(state)
            });
            return Results.Redirect(DiscordApi + "/oauth2/authorize?" + query);
        });

        app.MapGet("/auth/callback", async (
            HttpContext ctx,
            IOptions<DiscordOptions> opt,
            SessionTokenService tokens,
            IHttpClientFactory httpFactory,
            ILoggerFactory logFactory) =>
        {
            var d = opt.Value;
            var log = logFactory.CreateLogger("AuthCallback");
            var q = ctx.Request.Query;
            var savedState = ctx.Request.Cookies[StateCookie];

            if (!string.IsNullOrEmpty(q["error"]))
                return RedirectToApp(ctx, d, "/", "error", q["error"]!);

            var code = q["code"].ToString();
            var state = q["state"].ToString();
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state) ||
                string.IsNullOrEmpty(savedState) || !FixedEquals(state, savedState))
                return RedirectToApp(ctx, d, "/", "error", "state_mismatch");

            if (string.IsNullOrEmpty(d.ClientSecret))
                return RedirectToApp(ctx, d, "/", "error", "worker_misconfigured");

            var http = httpFactory.CreateClient();

            // Exchange the code for a user access token.
            TokenResponse? tok;
            try
            {
                var form = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = d.ClientId,
                    ["client_secret"] = d.ClientSecret,
                    ["grant_type"] = "authorization_code",
                    ["code"] = code,
                    ["redirect_uri"] = d.RedirectUri
                });
                var r = await http.PostAsync(DiscordApi + "/oauth2/token", form);
                if (!r.IsSuccessStatusCode)
                    return RedirectToApp(ctx, d, "/", "error", "token_http_" + (int)r.StatusCode);
                tok = await r.Content.ReadFromJsonAsync<TokenResponse>();
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Discord token exchange failed");
                return RedirectToApp(ctx, d, "/", "error", "token_exchange_failed");
            }
            if (string.IsNullOrEmpty(tok?.AccessToken))
                return RedirectToApp(ctx, d, "/", "error", "token_exchange_failed");

            // Read the signed-in user's membership (incl. roles) in the guild.
            GuildMember? member;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get,
                    DiscordApi + $"/users/@me/guilds/{d.GuildId}/member");
                req.Headers.Authorization = new("Bearer", tok.AccessToken);
                var r = await http.SendAsync(req);
                if (r.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return RedirectToApp(ctx, d, "/", "denied", "not_in_server");
                if (!r.IsSuccessStatusCode)
                    return RedirectToApp(ctx, d, "/", "error", "member_http_" + (int)r.StatusCode);
                member = await r.Content.ReadFromJsonAsync<GuildMember>();
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Discord member fetch failed");
                return RedirectToApp(ctx, d, "/", "error", "member_fetch_failed");
            }

            var roles = member?.Roles ?? Array.Empty<string>();
            var isOfficer = roles.Any(r => d.OfficerRoleIds.Contains(r));
            var hasHome = isOfficer || roles.Contains(d.HomeRoleId);
            if (!hasHome)
                return RedirectToApp(ctx, d, "/", "denied", "no_access");

            var user = member?.User;
            var uid = user?.Id ?? "";
            var name = user?.GlobalName ?? user?.Username ?? "Raider";
            var nick = string.IsNullOrWhiteSpace(member?.Nick) ? null : member!.Nick;

            // Upsert the Member row when a database is available. The DbContext is
            // registered only when a connection string is set, so resolve optionally
            // — login still works before Postgres is wired up.
            var db = ctx.RequestServices.GetService<AppDbContext>();
            if (db is not null && !string.IsNullOrEmpty(uid))
            {
                try
                {
                    var existing = await db.Members.FirstOrDefaultAsync(m => m.DiscordUserId == uid);
                    if (existing is null)
                    {
                        db.Members.Add(new Member
                        {
                            DiscordUserId = uid,
                            DiscordUsername = user?.Username,
                            DisplayName = name,
                            Nickname = nick,
                            LastSeenAt = DateTimeOffset.UtcNow
                        });
                    }
                    else
                    {
                        existing.DiscordUsername = user?.Username;
                        existing.DisplayName = name;
                        existing.Nickname = nick;
                        existing.LastSeenAt = DateTimeOffset.UtcNow;
                    }
                    await db.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    // Don't fail login on a persistence hiccup — the session is what gates access.
                    log.LogError(ex, "Member upsert failed for {Uid}", uid);
                }
            }

            var session = tokens.Sign(uid, name, isOfficer);
            return RedirectToApp(ctx, d, "/home.html", "session", session, clearState: true);
        });

        // Sliding renewal. The pages call this on load once a session is past its
        // halfway point, so an active raider never gets bounced back to Discord.
        // Only a still-valid token can be renewed, and only until the session hits
        // its absolute lifetime cap — after that a real sign-in re-reads roles.
        app.MapPost("/auth/refresh", (HttpContext ctx, SessionTokenService tokens) =>
        {
            var (session, err) = ctx.RequireSession(tokens);
            if (err is not null) return err;

            var renewed = tokens.Renew(session!);
            return renewed is null
                ? Results.Json(new { error = "unauthorized", detail = "Session too old — sign in again." }, statusCode: 401)
                : Results.Json(new { session = renewed });
        });
    }

    // Redirect the browser back to the static site, passing a result in the URL
    // fragment (fragments never reach a server, so a token stays out of request logs).
    private static IResult RedirectToApp(HttpContext ctx, DiscordOptions d,
        string path, string key, string value, bool clearState = false)
    {
        if (clearState || key is "error" or "denied")
        {
            ctx.Response.Cookies.Append(StateCookie, "", new CookieOptions
            {
                HttpOnly = true,
                Secure = ctx.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                Path = "/",
                MaxAge = TimeSpan.Zero
            });
        }
        var location = d.AppBase.TrimEnd('/') + path + "#" + key + "=" + Uri.EscapeDataString(value);
        return Results.Redirect(location);
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static bool FixedEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));
}
