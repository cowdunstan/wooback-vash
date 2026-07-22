using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using WoobackVash.Api.Config;

namespace WoobackVash.Api.Services;

/// <summary>
/// Pulls the guild roster from the Blizzard Game Data API, keeping the credentials
/// server-side. Same shape as <see cref="WarcraftLogsService"/>: client-credentials
/// OAuth with the token cached to expiry, a cached roster with a freshness window
/// (CacheTtlSeconds) that officers can force past, and a stale-copy fallback when
/// Blizzard times out or rate-limits us. Feeds the members page's guild sync.
///
/// It also resolves item names to item ids (see ResolveItemIdsAsync), which is what
/// lets the loot-prio page turn a sheet full of item *names* into a Gargul
/// soft-reserve export, which is keyed by id.
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

    /* ── Item names → item ids ───────────────────────────────────────────────
       The loot sheet names items and nothing else, but a Gargul soft-reserve
       export is keyed by item id, so the two have to be bridged. Blizzard's item
       search is the bridge, on the *static* classicann namespace — TBC's item
       table, so "Cuffs of Devastation" is 30870 and not whatever retail reused
       the name for.

       The search is a fuzzy one: asking for a name returns a page of loosely
       related items, so the caller's name has to be re-matched against the
       results rather than trusted. Three passes, narrowest first:
         1. an exact name match (normalised: case, spaces and punctuation);
         2. a single-typo match, because the sheet is hand-typed and has a few
            ("Bracers of Martydom", "Antonida's Aegis", "Mymidon's Treads");
         3. the same again with trailing words dropped, which is what turns the
            sheet's annotated rows ("Shroud of the Highborne Healer Prio") into
            the item they mean.
       Anything still unmatched comes back unresolved and is reported, never
       guessed at. */

    private readonly ConcurrentDictionary<string, long> _itemIds = new();

    /// <summary>Resolved ids by the name that was asked for, plus the names that
    /// could not be resolved at all. Results are cached for the process lifetime:
    /// an item's id never changes.</summary>
    public async Task<(int Status, Dictionary<string, long>? Ids, List<string>? Unresolved, string? Error)>
        ResolveItemIdsAsync(IEnumerable<string> names)
    {
        var wanted = names.Select(n => (n ?? "").Trim())
                          .Where(n => n.Length > 0)
                          .Distinct(StringComparer.OrdinalIgnoreCase)
                          .ToList();

        var resolved = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var unresolved = new List<string>();

        var todo = new List<string>();
        foreach (var n in wanted)
        {
            if (_itemIds.TryGetValue(CacheKey(n), out var cached))
            {
                if (cached > 0) resolved[n] = cached; else unresolved.Add(n);
            }
            else todo.Add(n);
        }
        if (todo.Count == 0) return (200, resolved, unresolved, null);

        var token = await GetTokenAsync();
        if (token is null)
            return (501, null, null, "Blizzard API credentials are not set on the server yet.");

        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(_opt.RequestTimeoutSeconds);

        // A raid tab is ~150 names on a cold cache. Eight at a time keeps that
        // to a couple of seconds without leaning on Blizzard's rate limit.
        using var gate = new SemaphoreSlim(8);
        var found = new ConcurrentDictionary<string, long>();

        await Task.WhenAll(todo.Select(async name =>
        {
            await gate.WaitAsync();
            try { found[name] = await SearchItemIdAsync(http, token, name); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Blizzard item search failed for {Name}", name);
                found[name] = 0;
            }
            finally { gate.Release(); }
        }));

        foreach (var name in todo)
        {
            var id = found.TryGetValue(name, out var v) ? v : 0;
            _itemIds[CacheKey(name)] = id;
            if (id > 0) resolved[name] = id; else unresolved.Add(name);
        }

        return (200, resolved, unresolved, null);
    }

    private static string CacheKey(string name) => Normalize(name);

    /// <summary>Case, spaces and punctuation folded away, so "Antonida's" and
    /// "Antonidas's" differ by exactly the one letter that is really wrong.</summary>
    private static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.ToLowerInvariant())
            if (char.IsAsciiLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }

    /// <summary>One insertion, deletion or substitution apart.</summary>
    private static bool OneTypoApart(string a, string b)
    {
        if (a == b) return true;
        if (Math.Abs(a.Length - b.Length) > 1) return false;

        int head = 0;
        while (head < a.Length && head < b.Length && a[head] == b[head]) head++;
        int tail = 0;
        while (tail < a.Length - head && tail < b.Length - head &&
               a[a.Length - 1 - tail] == b[b.Length - 1 - tail]) tail++;
        return a.Length - head - tail <= 1 && b.Length - head - tail <= 1;
    }

    private async Task<long> SearchItemIdAsync(HttpClient http, string token, string name)
    {
        // The Warglaives are the one case where the sheet distinguishes two items
        // that share a name, as "… (MH)" and "… (OH)". Strip the marker to search,
        // then pick between the two same-named results by id — main hand first.
        var hand = 0;
        var query = name;
        var m = Regex.Match(name, @"^(.*?)\s*\((MH|OH)\)\s*$", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            query = m.Groups[1].Value;
            hand = m.Groups[2].Value.Equals("OH", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        }

        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

        // Drop at most two trailing words, so an annotated row still finds its item.
        for (var drop = 0; drop <= 2 && words.Count - drop >= 2; drop++)
        {
            var attempt = string.Join(' ', words.Take(words.Count - drop));
            var Results = await SearchAsync(http, token, attempt);
            if (Results.Count == 0) continue;

            var target = Normalize(attempt);
            var exact = Results.Where(r => Normalize(r.Name) == target).OrderBy(r => r.Id).ToList();
            if (exact.Count > 0) return exact[Math.Min(hand, exact.Count - 1)].Id;

            var close = Results.Where(r => OneTypoApart(Normalize(r.Name), target)).OrderBy(r => r.Id).ToList();
            if (close.Count > 0) return close[Math.Min(hand, close.Count - 1)].Id;
        }
        return 0;
    }

    private record ItemHit(long Id, string Name);

    private async Task<List<ItemHit>> SearchAsync(HttpClient http, string token, string name)
    {
        var url = $"{_opt.ApiHost.TrimEnd('/')}/data/wow/search/item" +
                  $"?namespace={_opt.StaticNamespace}" +
                  $"&name.{_opt.Locale}={Uri.EscapeDataString(name)}" +
                  $"&_pageSize=100&locale={_opt.Locale}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var r = await http.SendAsync(req);
        if (!r.IsSuccessStatusCode) return new List<ItemHit>();

        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        var hits = new List<ItemHit>();
        if (!doc.RootElement.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array) return hits;

        foreach (var entry in results.EnumerateArray())
        {
            if (!entry.TryGetProperty("data", out var data)) continue;
            if (!data.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out var id)) continue;
            if (!data.TryGetProperty("name", out var nameEl)) continue;

            // The search returns names keyed by locale.
            var itemName = nameEl.ValueKind == JsonValueKind.String
                ? nameEl.GetString()!
                : GetString(nameEl, _opt.Locale);
            if (!string.IsNullOrEmpty(itemName)) hits.Add(new ItemHit(id, itemName));
        }
        return hits;
    }

    private static string GetString(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static int GetInt(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;
}
