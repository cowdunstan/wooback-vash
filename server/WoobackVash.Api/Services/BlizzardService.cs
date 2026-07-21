using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using WoobackVash.Api.Config;

namespace WoobackVash.Api.Services;

/// <summary>
/// Pulls the guild roster from the Blizzard Game Data API, keeping the credentials
/// server-side. Same shape as <see cref="WarcraftLogsService"/>: client-credentials
/// OAuth with the token cached to expiry, a cached roster with a freshness window
/// (CacheTtlSeconds) that officers can force past, and a stale-copy fallback when
/// Blizzard times out or rate-limits us. Feeds the members page's guild sync.
/// </summary>
public class BlizzardService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly BlizzardOptions _opt;
    private readonly ILogger<BlizzardService> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _token;
    private long _tokenExpUnix;

    private List<RosterMember>? _cachedRoster;
    private DateTimeOffset _cachedAt;

    public BlizzardService(
        IHttpClientFactory httpFactory,
        IOptions<BlizzardOptions> opt,
        ILogger<BlizzardService> log)
    {
        _httpFactory = httpFactory;
        _opt = opt.Value;
        _log = log;
    }

    /// <summary>One character on the guild roster. Rank 0 is the guild master.</summary>
    public record RosterMember(string Name, string? RealmSlug, int Rank, int Level, int ClassId);

    private static long NowUnix() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private bool CacheFresh() =>
        _cachedRoster is not null && (DateTimeOffset.UtcNow - _cachedAt).TotalSeconds < _opt.CacheTtlSeconds;

    /// <summary>Returns (status, roster, error). On success error is null; on failure
    /// roster is null and error carries a message safe to show an officer.
    /// <paramref name="forceRefresh"/> bypasses the cache TTL.</summary>
    public async Task<(int Status, List<RosterMember>? Roster, string? Error)> GetGuildRosterAsync(bool forceRefresh = false)
    {
        if (!forceRefresh && CacheFresh()) return (200, _cachedRoster, null);

        await _gate.WaitAsync();
        try
        {
            // Another caller may have refreshed while we waited (a forced refresh always
            // goes to the network, so it never short-circuits here).
            if (!forceRefresh && CacheFresh()) return (200, _cachedRoster, null);

            var token = await GetTokenAsync();
            if (token is null)
                return (501, null, "Blizzard API credentials are not set on the server yet.");

            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(_opt.RequestTimeoutSeconds);

            using var req = new HttpRequestMessage(HttpMethod.Get, _opt.RosterUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            HttpResponseMessage r;
            try { r = await http.SendAsync(req); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Upstream stalled or hit our timeout — serve a prior copy rather than
                // leaving the officer's sync spinning.
                _log.LogWarning(ex, "Blizzard roster request failed/timed out");
                if (_cachedRoster is not null) return (200, _cachedRoster, null);
                return (504, null, "Blizzard is not responding right now. Try again in a minute.");
            }

            if (r.StatusCode == (HttpStatusCode)429)
            {
                if (_cachedRoster is not null) return (200, _cachedRoster, null);
                return (429, null, "Blizzard is rate-limiting the guild tools right now. Try again in a minute.");
            }
            if (r.StatusCode == HttpStatusCode.NotFound)
                return (404, null, $"Blizzard has no guild \"{_opt.GuildSlug}\" on realm \"{_opt.RealmSlug}\" " +
                                   $"in namespace {_opt.Namespace} — check the realm and guild slugs.");
            if (r.StatusCode == HttpStatusCode.Unauthorized || r.StatusCode == HttpStatusCode.Forbidden)
                return (502, null, "Blizzard rejected the API credentials.");
            if (!r.IsSuccessStatusCode)
                return (502, null, "Blizzard API returned " + (int)r.StatusCode);

            List<RosterMember> roster;
            try { roster = ParseRoster(await r.Content.ReadAsStringAsync()); }
            catch (JsonException ex)
            {
                _log.LogWarning(ex, "Blizzard roster parse failed");
                return (502, null, "Unexpected Blizzard roster response.");
            }

            _cachedRoster = roster;
            _cachedAt = DateTimeOffset.UtcNow;
            return (200, roster, null);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Blizzard roster fetch failed");
            return (502, null, "Blizzard roster fetch failed: " + ex.GetBaseException().Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    // members[] → { rank, character: { name, level, realm:{ slug }, playable_class:{ id } } }
    private static List<RosterMember> ParseRoster(string json)
    {
        var list = new List<RosterMember>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("members", out var members) ||
            members.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var m in members.EnumerateArray())
        {
            if (!m.TryGetProperty("character", out var ch) || ch.ValueKind != JsonValueKind.Object) continue;
            var name = GetString(ch, "name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            list.Add(new RosterMember(
                name,
                ch.TryGetProperty("realm", out var realm) && realm.ValueKind == JsonValueKind.Object
                    ? GetString(realm, "slug") : null,
                GetInt(m, "rank"),
                GetInt(ch, "level"),
                ch.TryGetProperty("playable_class", out var cls) && cls.ValueKind == JsonValueKind.Object
                    ? GetInt(cls, "id") : 0));
        }
        return list;
    }

    private async Task<string?> GetTokenAsync()
    {
        var now = NowUnix();
        if (_token is not null && _tokenExpUnix - 60 > now) return _token;
        if (string.IsNullOrWhiteSpace(_opt.ClientId) || string.IsNullOrWhiteSpace(_opt.ClientSecret)) return null;

        try
        {
            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(_opt.RequestTimeoutSeconds);

            using var req = new HttpRequestMessage(HttpMethod.Post, _opt.OAuthUrl);
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_opt.ClientId}:{_opt.ClientSecret}"));
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
            req.Content = new StringContent("grant_type=client_credentials", Encoding.UTF8,
                "application/x-www-form-urlencoded");

            var r = await http.SendAsync(req);
            if (!r.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("access_token", out var at) ||
                at.ValueKind != JsonValueKind.String) return null;

            var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var ei) &&
                            ei.TryGetInt64(out var v) ? v : 3600;
            _token = at.GetString();
            _tokenExpUnix = now + expiresIn;
            return _token;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Blizzard token request failed");
            return null;
        }
    }

    private static string GetString(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static int GetInt(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;
}
