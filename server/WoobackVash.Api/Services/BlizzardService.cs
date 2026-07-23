using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using WoobackVash.Api.Config;
using WoobackVash.Api.Models;

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

    /* ── Live character gear ─────────────────────────────────────────────────
       The character-equipment profile is what a gear "refresh" pulls first: it is
       the freshest possible snapshot — whatever the character is wearing right now,
       no raid required — where the log only ever knew the last night they raided.
       Blizzard's character-profile routes are the less dependable ones on the
       Anniversary realms (see CharacterGearSnapshot), which is why the caller falls
       back to Warcraft Logs on anything but a clean 200. Not cached: a refresh is an
       explicit request for the current state, and it is one character at a time. */

    /// <summary>Pulls a character's currently equipped gear (items, enchants, gems)
    /// from Blizzard, normalized into the same <see cref="PlayerGear"/> the log path
    /// produces so the snapshot store and character sheet don't care which source it
    /// came from. The equipment route carries no spec/role, so those are null. Returns
    /// (status, gear, error); 404 when Blizzard has no such character (the signal to
    /// fall back to Warcraft Logs), 501 when credentials aren't set.</summary>
    public async Task<(int Status, PlayerGear? Gear, string? Error)> GetCharacterEquipmentAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return (400, null, "A character name is required.");

        var token = await GetTokenAsync();
        if (token is null)
            return (501, null, "Blizzard API credentials are not set on the server yet.");

        var url = $"{_opt.ApiHost.TrimEnd('/')}/profile/wow/character/{_opt.RealmSlug}/" +
                  $"{Uri.EscapeDataString(name.ToLowerInvariant())}/equipment" +
                  $"?namespace={_opt.Namespace}&locale={_opt.Locale}";

        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(_opt.RequestTimeoutSeconds);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage r;
        try { r = await http.SendAsync(req); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _log.LogWarning(ex, "Blizzard equipment request failed/timed out for {Name}", name);
            return (504, null, "Blizzard is not responding right now. Try again in a minute.");
        }

        if (r.StatusCode == HttpStatusCode.NotFound)
            return (404, null, $"Blizzard has no character \"{name}\" on {_opt.RealmSlug}.");
        if (r.StatusCode == (HttpStatusCode)429)
            return (429, null, "Blizzard is rate-limiting the guild tools right now. Try again in a minute.");
        if (r.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return (502, null, "Blizzard rejected the API credentials.");
        if (!r.IsSuccessStatusCode)
            return (502, null, "Blizzard API returned " + (int)r.StatusCode);

        try { return (200, ParseEquipment(name, await r.Content.ReadAsStringAsync()), null); }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "Blizzard equipment parse failed for {Name}", name);
            return (502, null, "Unexpected Blizzard equipment response.");
        }
    }

    // equipped_items[] → { item:{id}, slot:{type}, quality:{type}, name,
    // enchantments:[{ enchantment_id, display_string, enchantment_slot:{type} }],
    // sockets:[{ item:{id} }] }. The classic profile route carries no per-item level
    // and no icon (media needs another call), so both are left blank — the sheet omits
    // the item-level line and names/illustrates items from Wowhead regardless. Each
    // slot holds exactly one item (no mid-night swaps like a log), so the missing ilvl
    // doesn't affect ordering.
    private static PlayerGear ParseEquipment(string name, string json)
    {
        var items = new List<GearItem>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("equipped_items", out var equipped) &&
            equipped.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in equipped.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var id = e.TryGetProperty("item", out var it) && it.ValueKind == JsonValueKind.Object
                    ? GetLong(it, "id") : 0;
                if (id <= 0) continue;

                long? perm = null, temp = null;
                string? permName = null, tempName = null;
                if (e.TryGetProperty("enchantments", out var enchants) && enchants.ValueKind == JsonValueKind.Array)
                {
                    foreach (var en in enchants.EnumerateArray())
                    {
                        var slotType = en.TryGetProperty("enchantment_slot", out var es) &&
                                       es.ValueKind == JsonValueKind.Object ? GetString(es, "type") : "";
                        var enchId = GetLong(en, "enchantment_id");
                        var display = CleanEnchant(GetString(en, "display_string"));
                        if (slotType.Equals("TEMPORARY", StringComparison.OrdinalIgnoreCase))
                        {
                            temp = enchId != 0 ? enchId : temp;
                            tempName ??= display;
                        }
                        else // PERMANENT (and anything else) folds into the permanent enchant
                        {
                            perm = enchId != 0 ? enchId : perm;
                            permName ??= display;
                        }
                    }
                }

                var gems = new List<long>();
                if (e.TryGetProperty("sockets", out var sockets) && sockets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in sockets.EnumerateArray())
                    {
                        var gemId = s.TryGetProperty("item", out var gi) && gi.ValueKind == JsonValueKind.Object
                            ? GetLong(gi, "id") : 0;
                        if (gemId > 0) gems.Add(gemId);
                    }
                }

                // Newer expansions expose level.value here; the classic route omits it,
                // so this stays null and the sheet simply shows no item level.
                double? ilvl = e.TryGetProperty("level", out var lvl) && lvl.ValueKind == JsonValueKind.Object
                    ? GetDouble(lvl, "value") : 0;
                if (ilvl == 0) ilvl = null;

                items.Add(new GearItem(
                    SlotName(e),
                    id,
                    NullIfEmpty(GetString(e, "name")),
                    null,
                    QualityRank(e),
                    ilvl,
                    perm, permName, temp, tempName, gems));
            }
        }

        // Average item level: the mean of the rated pieces, matching the log path's
        // fallback (0-ilvl entries like a tabard would only drag it down).
        double? avg = null;
        var rated = items.Where(i => i.ItemLevel is > 0).Select(i => i.ItemLevel!.Value).ToList();
        if (rated.Count > 0) avg = Math.Round(rated.Average(), 1);

        return new PlayerGear(name, null, null, null, null, avg, items);
    }

    // Blizzard slot types (HEAD, MAIN_HAND, FINGER_1, …) fold to the lowercase,
    // underscore-free slot names the character sheet and item pages key on
    // (head, mainhand, finger1, …).
    private static string SlotName(JsonElement item) =>
        item.TryGetProperty("slot", out var slot) && slot.ValueKind == JsonValueKind.Object
            ? GetString(slot, "type").ToLowerInvariant().Replace("_", "")
            : "unknown";

    // quality.type (POOR…HEIRLOOM) → the 0-based rank the log path stores.
    private static int? QualityRank(JsonElement item)
    {
        if (!item.TryGetProperty("quality", out var q) || q.ValueKind != JsonValueKind.Object) return null;
        return GetString(q, "type").ToUpperInvariant() switch
        {
            "POOR" => 0,
            "COMMON" => 1,
            "UNCOMMON" => 2,
            "RARE" => 3,
            "EPIC" => 4,
            "LEGENDARY" => 5,
            "ARTIFACT" => 6,
            "HEIRLOOM" => 7,
            _ => null
        };
    }

    // Blizzard prefixes an enchant string with a label ("Enchanted: +26 …"); drop it
    // so the sheet reads like the log's bare enchant name.
    private static string? CleanEnchant(string s)
    {
        s = s.Trim();
        var colon = s.IndexOf(": ", StringComparison.Ordinal);
        if (colon >= 0 && colon <= 12) s = s[(colon + 2)..].Trim();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

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

    private static long GetLong(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    private static double GetDouble(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) ? n : 0;
}
