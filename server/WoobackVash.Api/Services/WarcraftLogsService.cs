using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using WoobackVash.Api.Config;
using WoobackVash.Api.Models;

namespace WoobackVash.Api.Services;

/// <summary>
/// Pulls the guild's Warcraft Logs report list, keeping the API credentials
/// server-side. Ported from handleWclReports in raidhelper-proxy.worker.js:
/// client-credentials OAuth (token cached to expiry), a single-page fetch of the
/// newest reports, a long freshness window (CacheTtlSeconds, default 30 min) to
/// stay under the v2 hourly points budget, and a stale-copy fallback when
/// Warcraft Logs rate-limits us (429). Officers can force a refresh past the TTL.
/// </summary>
public class WarcraftLogsService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly WarcraftLogsOptions _opt;
    private readonly ILogger<WarcraftLogsService> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _token;
    private long _tokenExpUnix;

    private string? _cachedBody;
    private DateTimeOffset _cachedAt;

    public WarcraftLogsService(
        IHttpClientFactory httpFactory,
        IOptions<WarcraftLogsOptions> opt,
        ILogger<WarcraftLogsService> log)
    {
        _httpFactory = httpFactory;
        _opt = opt.Value;
        _log = log;
    }

    private static long NowUnix() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private bool CacheFresh() =>
        _cachedBody is not null && (DateTimeOffset.UtcNow - _cachedAt).TotalSeconds < _opt.CacheTtlSeconds;

    /// <summary>Returns (httpStatus, jsonBody). Body is always JSON — either the
    /// report list or an { error, detail } object matching the Worker's shape.
    /// The page shows only the newest logs, so we fetch a single page of
    /// ReportLimit reports (newest-first) — one upstream call per refresh.
    /// <paramref name="forceRefresh"/> (officers only) bypasses the cache TTL.</summary>
    public async Task<(int Status, string Body)> GetReportsAsync(bool forceRefresh = false)
    {
        if (!forceRefresh && CacheFresh()) return (200, _cachedBody!);

        if (string.IsNullOrWhiteSpace(_opt.GuildServer) || string.IsNullOrWhiteSpace(_opt.GuildRegion))
            return (501, Err("not_configured", "Warcraft Logs guild is not set on the server yet."));

        await _gate.WaitAsync();
        try
        {
            // Another caller may have refreshed while we waited (a forced refresh
            // always goes to the network, so it never short-circuits here).
            if (!forceRefresh && CacheFresh()) return (200, _cachedBody!);

            var token = await GetTokenAsync();
            if (token is null)
                return (501, Err("not_configured", "Warcraft Logs API credentials are not set on the server yet."));

            const string query =
                "query($name:String!,$server:String!,$region:String!,$limit:Int!){" +
                "reportData{reports(guildName:$name,guildServerSlug:$server,guildServerRegion:$region,limit:$limit,page:1){" +
                "data{code title startTime endTime zone{name} owner{name}}}}}";

            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(_opt.RequestTimeoutSeconds);
            var reports = new List<object>();

            using var req = new HttpRequestMessage(HttpMethod.Post, _opt.GraphQlUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var payload = JsonSerializer.Serialize(new
            {
                query,
                variables = new { name = _opt.GuildName, server = _opt.GuildServer, region = _opt.GuildRegion, limit = _opt.ReportLimit }
            });
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            HttpResponseMessage r;
            try
            {
                r = await http.SendAsync(req);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Upstream stalled or hit our per-request timeout — fall back to a
                // prior cached copy rather than leaving the browser spinning.
                _log.LogWarning(ex, "Warcraft Logs request failed/timed out");
                if (_cachedBody is not null) return (200, _cachedBody);
                return (504, Err("upstream_timeout",
                    "Warcraft Logs is not responding right now. Try again in a minute."));
            }

            // Rate limited — the hourly points budget is spent. Serve a stale
            // cached copy if we have one, else ask the caller to wait it out.
            if (r.StatusCode == (HttpStatusCode)429)
            {
                if (_cachedBody is not null) return (200, _cachedBody);
                return (429, Err("rate_limited",
                    "Warcraft Logs is rate-limiting the guild tools right now. Try again in a minute."));
            }
            if (!r.IsSuccessStatusCode)
                return (502, Err("upstream", "Warcraft Logs API returned " + (int)r.StatusCode));

            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            if (!TryGetReportsNode(doc.RootElement, out var node))
            {
                var gqlErr = TryGetGqlError(doc.RootElement);
                return (502, Err("upstream", gqlErr ?? "Unexpected Warcraft Logs response."));
            }

            if (node.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var rep in data.EnumerateArray())
                {
                    var code = GetString(rep, "code");
                    reports.Add(new
                    {
                        code,
                        title = GetString(rep, "title"),
                        startTime = GetLong(rep, "startTime"),
                        endTime = GetLong(rep, "endTime"),
                        zone = GetNestedName(rep, "zone"),
                        owner = GetNestedName(rep, "owner"),
                        url = _opt.Host.TrimEnd('/') + "/reports/" + code
                    });
                }
            }

            var guildUrl = $"{_opt.Host.TrimEnd('/')}/guild/{_opt.GuildRegion.ToLowerInvariant()}/" +
                           $"{_opt.GuildServer}/{Uri.EscapeDataString(_opt.GuildName)}";
            var body = JsonSerializer.Serialize(new { guild = _opt.GuildName, guildUrl, reports });

            _cachedBody = body;
            _cachedAt = DateTimeOffset.UtcNow;
            return (200, body);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Warcraft Logs fetch failed");
            return (502, Err("upstream fetch failed", ex.Message));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A guild-tagged player found in a Warcraft Logs report.</summary>
    public record ReportPlayer(string Name, string Realm, string? Cls);

    /// <summary>The roster pulled from one report, filtered to wooback members.</summary>
    public record ReportPlayers(string Code, string? Title, long StartTime, string? Zone,
        List<ReportPlayer> Players, int FilteredOut);

    /// <summary>Pulls the participant roster from a single Warcraft Logs report and
    /// keeps only the characters WCL lists as members of our guild (per-character
    /// <c>guilds</c> lookup). Feeds the attendance import: present players become
    /// attendance rows, unknown ones become unclaimed characters. Two upstream calls
    /// — the report's actors, then one batched character-guild query — so it is
    /// officer-gated at the endpoint. Returns (status, result, error): on success
    /// <paramref name="error"/> is null; on failure result is null.</summary>
    public async Task<(int Status, ReportPlayers? Result, string? Error)> GetReportPlayersAsync(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return (400, null, "A report code is required.");

        var token = await GetTokenAsync();
        if (token is null)
            return (501, null, "Warcraft Logs API credentials are not set on the server yet.");

        // ── Query A: the report's player roster ──────────────────────────────
        const string reportQuery =
            "query($code:String!){reportData{report(code:$code){" +
            "title startTime endTime zone{name} " +
            "masterData{actors(type:\"Player\"){name server subType}}}}}";

        var (rStatus, rDoc, rErr) = await PostGraphQlAsync(token,
            JsonSerializer.Serialize(new { query = reportQuery, variables = new { code } }));
        if (rDoc is null) return (rStatus, null, rErr);
        using var reportDoc = rDoc;

        if (!reportDoc.RootElement.TryGetProperty("data", out var rData) ||
            !rData.TryGetProperty("reportData", out var rd) ||
            !rd.TryGetProperty("report", out var report) ||
            report.ValueKind != JsonValueKind.Object)
        {
            var gqlErr = TryGetGqlError(reportDoc.RootElement);
            return (404, null, gqlErr ?? "No such Warcraft Logs report, or it is private.");
        }

        var title = GetString(report, "title");
        var startTime = GetLong(report, "startTime");
        var zone = GetNestedName(report, "zone");

        // Distinct players by name (a name can appear once per report; dedupe defensively).
        var actors = new List<ReportPlayer>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (report.TryGetProperty("masterData", out var master) &&
            master.TryGetProperty("actors", out var actorArr) &&
            actorArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in actorArr.EnumerateArray())
            {
                var name = GetString(a, "name");
                if (string.IsNullOrWhiteSpace(name) || !seen.Add(name)) continue;
                actors.Add(new ReportPlayer(name, GetString(a, "server"), NullIfEmpty(GetString(a, "subType"))));
            }
        }

        if (actors.Count == 0)
            return (200, new ReportPlayers(code, NullIfEmpty(title), startTime, NullIfEmpty(zone),
                new List<ReportPlayer>(), 0), null);

        // ── Query B: which of those are in our guild? (one batched request) ──
        // Alias one character(...) field per player; keep those whose guild list
        // contains our guild name. A null node = unknown character = excluded.
        var region = _opt.GuildRegion;
        var sb = new StringBuilder("query{characterData{");
        for (var i = 0; i < actors.Count; i++)
        {
            sb.Append($"c{i}:character(name:{JsonSerializer.Serialize(actors[i].Name)},")
              .Append($"serverSlug:{JsonSerializer.Serialize(actors[i].Realm)},")
              .Append($"serverRegion:{JsonSerializer.Serialize(region)}){{guilds{{name}}}} ");
        }
        sb.Append("}}");

        var (gStatus, gDoc, gErr) = await PostGraphQlAsync(token,
            JsonSerializer.Serialize(new { query = sb.ToString() }));
        if (gDoc is null) return (gStatus, null, gErr);
        using var guildDoc = gDoc;

        if (!guildDoc.RootElement.TryGetProperty("data", out var gData) ||
            !gData.TryGetProperty("characterData", out var cData) ||
            cData.ValueKind != JsonValueKind.Object)
        {
            var gqlErr = TryGetGqlError(guildDoc.RootElement);
            return (502, null, gqlErr ?? "Could not verify guild membership on Warcraft Logs.");
        }

        var kept = new List<ReportPlayer>();
        for (var i = 0; i < actors.Count; i++)
        {
            if (cData.TryGetProperty($"c{i}", out var ch) &&
                ch.ValueKind == JsonValueKind.Object &&
                InGuild(ch)) kept.Add(actors[i]);
        }

        return (200, new ReportPlayers(code, NullIfEmpty(title), startTime, NullIfEmpty(zone),
            kept, actors.Count - kept.Count), null);
    }

    // The gear DTOs (GearItem, PlayerGear) live in Models/GearData.cs — both this
    // service and BlizzardService produce them, and GearSnapshotStore writes them.

    // WoW equipment slot order as the combat log reports it (Classic has no
    // ranged-slot ammo/relic split beyond index 17). The one place this mapping lives.
    private static readonly string[] SlotNames =
    {
        "head", "neck", "shoulder", "shirt", "chest", "waist", "legs", "feet",
        "wrist", "hands", "finger1", "finger2", "trinket1", "trinket2", "back",
        "mainhand", "offhand", "ranged", "tabard"
    };

    /// <summary>Pulls every player's gear (with enchants and gems), spec and role from
    /// one report. Feeds the gear snapshots behind the character sheet: the attendance
    /// import calls this after its own roster pull, so a failure here degrades to "no
    /// gear this time" rather than failing the import. One upstream call.
    /// Returns (status, result, error) like <see cref="GetReportPlayersAsync"/>.</summary>
    public async Task<(int Status, List<PlayerGear>? Result, string? Error)> GetReportGearAsync(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return (400, null, "A report code is required.");

        var token = await GetTokenAsync();
        if (token is null)
            return (501, null, "Warcraft Logs API credentials are not set on the server yet.");

        // playerDetails is an untyped JSON blob, so the whole report window is asked
        // for at once (times are ms offsets from the report start; a wide end covers
        // any night) and the shape is picked apart defensively below.
        const string query =
            "query($code:String!){reportData{report(code:$code){" +
            "playerDetails(startTime:0,endTime:999999999,includeCombatantInfo:true)}}}";

        var (status, doc, err) = await PostGraphQlAsync(token,
            JsonSerializer.Serialize(new { query, variables = new { code } }));
        if (doc is null) return (status, null, err);
        using var _doc = doc;

        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("reportData", out var rd) ||
            !rd.TryGetProperty("report", out var report) ||
            !report.TryGetProperty("playerDetails", out var details))
        {
            var gqlErr = TryGetGqlError(doc.RootElement);
            return (404, null, gqlErr ?? "No such Warcraft Logs report, or it is private.");
        }

        // The blob is usually wrapped: { data: { playerDetails: { tanks, healers, dps } } }.
        if (details.ValueKind == JsonValueKind.Object &&
            details.TryGetProperty("data", out var inner) &&
            inner.ValueKind == JsonValueKind.Object &&
            inner.TryGetProperty("playerDetails", out var unwrapped))
            details = unwrapped;

        if (details.ValueKind != JsonValueKind.Object)
            return (200, new List<PlayerGear>(), null);

        var players = new List<PlayerGear>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var bucket in details.EnumerateObject())
        {
            if (bucket.Value.ValueKind != JsonValueKind.Array) continue;
            var role = RoleFromBucket(bucket.Name);
            foreach (var p in bucket.Value.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Object) continue;
                var name = GetString(p, "name");
                if (string.IsNullOrWhiteSpace(name) || !seen.Add(name)) continue;
                players.Add(ParsePlayer(p, name, role));
            }
        }
        return (200, players, null);
    }

    /// <summary>Finds the most recent report the character appears in — anywhere,
    /// including a pug or another guild, not just our own logs — so an on-demand gear
    /// refresh can pull fresher gear than the last night they raided with us. The
    /// character lives on our realm/region (from config); returns the report's code
    /// and start time, or 404 when Warcraft Logs has no report for them. One upstream
    /// call; <see cref="GetReportGearAsync"/> then supplies the gear.</summary>
    public async Task<(int Status, (string Code, DateTimeOffset StartsAt)? Report, string? Error)>
        GetLatestReportCodeForCharacterAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return (400, null, "A character name is required.");

        var token = await GetTokenAsync();
        if (token is null)
            return (501, null, "Warcraft Logs API credentials are not set on the server yet.");

        const string query =
            "query($name:String!,$server:String!,$region:String!){characterData{" +
            "character(name:$name,serverSlug:$server,serverRegion:$region){" +
            "recentReports(limit:1){data{code startTime}}}}}";

        var (status, doc, err) = await PostGraphQlAsync(token, JsonSerializer.Serialize(new
        {
            query,
            variables = new { name, server = _opt.GuildServer, region = _opt.GuildRegion }
        }));
        if (doc is null) return (status, null, err);
        using var _doc = doc;

        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("characterData", out var cd) ||
            !cd.TryGetProperty("character", out var character) ||
            character.ValueKind != JsonValueKind.Object ||
            !character.TryGetProperty("recentReports", out var recent) ||
            !recent.TryGetProperty("data", out var list) ||
            list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
        {
            var gqlErr = TryGetGqlError(doc.RootElement);
            return (404, null, gqlErr ?? $"Warcraft Logs has no report for \"{name}\" on {_opt.GuildServer}.");
        }

        var first = list[0];
        var code = GetString(first, "code");
        if (string.IsNullOrWhiteSpace(code))
            return (404, null, $"Warcraft Logs has no report for \"{name}\" on {_opt.GuildServer}.");

        // startTime is Unix milliseconds; 0 falls back to now so the snapshot still sorts.
        var startMs = GetLong(first, "startTime");
        var startsAt = startMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(startMs) : DateTimeOffset.UtcNow;
        return (200, (code, startsAt), null);
    }

    // "tanks"/"healers"/"dps" → the role stored on the character. Anything else is
    // an unfamiliar bucket, so no role is claimed.
    private static string? RoleFromBucket(string bucket) => bucket.ToLowerInvariant() switch
    {
        "tanks" => "tank",
        "healers" => "healer",
        "dps" => "dps",
        _ => null
    };

    private static PlayerGear ParsePlayer(JsonElement p, string name, string? role)
    {
        var items = new List<GearItem>();
        if (p.TryGetProperty("combatantInfo", out var ci) && ci.ValueKind == JsonValueKind.Object &&
            ci.TryGetProperty("gear", out var gear) && gear.ValueKind == JsonValueKind.Array)
        {
            var index = -1;
            foreach (var g in gear.EnumerateArray())
            {
                index++;
                if (g.ValueKind != JsonValueKind.Object) continue;
                var id = GetLong(g, "id");
                if (id <= 0) continue; // empty slot

                // The log carries its own slot index (0..18, matching SlotNames); fall
                // back to position. Note a slot can repeat: playerDetails covers the
                // whole report, so swapping a neck mid-night yields two entries with
                // slot 1. Every one is kept — the sheet decides what to show.
                var slotIndex = g.TryGetProperty("slot", out var sl) &&
                                sl.ValueKind == JsonValueKind.Number && sl.TryGetInt32(out var si) ? si : index;

                var gems = new List<long>();
                if (g.TryGetProperty("gems", out var gemArr) && gemArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var gem in gemArr.EnumerateArray())
                    {
                        var gemId = gem.ValueKind == JsonValueKind.Object ? GetLong(gem, "id")
                                  : gem.ValueKind == JsonValueKind.Number && gem.TryGetInt64(out var gv) ? gv : 0;
                        if (gemId > 0) gems.Add(gemId);
                    }
                }

                items.Add(new GearItem(
                    SlotName(slotIndex),
                    id,
                    NullIfEmpty(GetString(g, "name")),
                    NullIfEmpty(GetString(g, "icon")),
                    NullIfZeroInt(g, "quality"),
                    NullIfZeroDouble(g, "itemLevel"),
                    NullIfZeroLong(g, "permanentEnchant"),
                    NullIfEmpty(GetString(g, "permanentEnchantName")),
                    NullIfZeroLong(g, "temporaryEnchant"),
                    NullIfEmpty(GetString(g, "temporaryEnchantName")),
                    gems));
            }
        }

        // Average item level: the log's own figure when it has one, else the mean of
        // what we parsed (0-ilvl entries, e.g. a tabard, would drag it down).
        double? ilvl = NullIfZeroDouble(p, "maxItemLevel") ?? NullIfZeroDouble(p, "minItemLevel");
        if (ilvl is null)
        {
            var rated = items.Where(i => i.ItemLevel is > 0).Select(i => i.ItemLevel!.Value).ToList();
            if (rated.Count > 0) ilvl = Math.Round(rated.Average(), 1);
        }

        return new PlayerGear(
            name,
            NullIfEmpty(GetString(p, "server")),
            NullIfEmpty(GetString(p, "type"))?.ToLowerInvariant(),
            ParseSpec(p),
            role,
            ilvl,
            items);
    }

    private static string SlotName(int index) =>
        index >= 0 && index < SlotNames.Length ? SlotNames[index] : "slot" + index;

    // specs is either ["Fury"] or [{ spec: "Fury" }]; icon ("Warrior-Fury") is the
    // last resort. Lower-cased to match the class/spec strings used elsewhere.
    private static string? ParseSpec(JsonElement p)
    {
        if (p.TryGetProperty("specs", out var specs) && specs.ValueKind == JsonValueKind.Array &&
            specs.GetArrayLength() > 0)
        {
            var first = specs[0];
            var s = first.ValueKind == JsonValueKind.String ? first.GetString()
                  : first.ValueKind == JsonValueKind.Object ? GetString(first, "spec")
                  : null;
            if (!string.IsNullOrWhiteSpace(s)) return s!.ToLowerInvariant();
        }

        var icon = GetString(p, "icon");
        var dash = icon.IndexOf('-');
        return dash >= 0 && dash < icon.Length - 1 ? icon[(dash + 1)..].ToLowerInvariant() : null;
    }

    private static int? NullIfZeroInt(JsonElement e, string prop)
    {
        var v = (int)GetLong(e, prop);
        return v == 0 ? null : v;
    }

    private static long? NullIfZeroLong(JsonElement e, string prop)
    {
        var v = GetLong(e, prop);
        return v == 0 ? null : v;
    }

    private static double? NullIfZeroDouble(JsonElement e, string prop)
    {
        var v = GetDouble(e, prop);
        return v == 0 ? null : v;
    }

    /// <summary>True if the character's <c>guilds</c> list contains our guild name.</summary>
    private bool InGuild(JsonElement character)
    {
        if (!character.TryGetProperty("guilds", out var guilds) || guilds.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var g in guilds.EnumerateArray())
        {
            if (GetString(g, "name").Equals(_opt.GuildName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>POSTs a GraphQL body to Warcraft Logs and parses the JSON response.
    /// Returns (status, doc, error): on success doc is non-null and the caller owns
    /// it (dispose); on failure doc is null and error carries a friendly message.</summary>
    private async Task<(int Status, JsonDocument? Doc, string? Error)> PostGraphQlAsync(string token, string body)
    {
        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(_opt.RequestTimeoutSeconds);

        using var req = new HttpRequestMessage(HttpMethod.Post, _opt.GraphQlUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        HttpResponseMessage r;
        try { r = await http.SendAsync(req); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _log.LogWarning(ex, "Warcraft Logs request failed/timed out");
            return (504, null, "Warcraft Logs is not responding right now. Try again in a minute.");
        }

        if (r.StatusCode == (HttpStatusCode)429)
            return (429, null, "Warcraft Logs is rate-limiting the guild tools right now. Try again in a minute.");
        if (!r.IsSuccessStatusCode)
            return (502, null, "Warcraft Logs API returned " + (int)r.StatusCode);

        return (200, JsonDocument.Parse(await r.Content.ReadAsStringAsync()), null);
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>Officer diagnostics: the guild's current v2 points budget
    /// ({ limitPerHour, pointsSpentThisHour, pointsResetIn }). Always live — the
    /// whole point is to see the budget right now — but the query itself costs a
    /// point or two, so it is officer-gated at the endpoint.</summary>
    public async Task<(int Status, string Body)> GetRateLimitAsync()
    {
        if (string.IsNullOrWhiteSpace(_opt.ClientId) || string.IsNullOrWhiteSpace(_opt.ClientSecret))
            return (501, Err("not_configured", "Warcraft Logs API credentials are not set on the server yet."));

        try
        {
            var token = await GetTokenAsync();
            if (token is null)
                return (501, Err("not_configured", "Warcraft Logs API credentials are not set on the server yet."));

            const string query = "query{rateLimitData{limitPerHour pointsSpentThisHour pointsResetIn}}";

            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(_opt.RequestTimeoutSeconds);

            using var req = new HttpRequestMessage(HttpMethod.Post, _opt.GraphQlUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Content = new StringContent(JsonSerializer.Serialize(new { query }), Encoding.UTF8, "application/json");

            HttpResponseMessage r;
            try { r = await http.SendAsync(req); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _log.LogWarning(ex, "Warcraft Logs rate-limit query failed/timed out");
                return (504, Err("upstream_timeout", "Warcraft Logs is not responding right now."));
            }

            if (r.StatusCode == (HttpStatusCode)429)
                return (429, Err("rate_limited",
                    "Warcraft Logs is rate-limiting this IP right now (too many requests). Try again shortly."));
            if (!r.IsSuccessStatusCode)
                return (502, Err("upstream", "Warcraft Logs API returned " + (int)r.StatusCode));

            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("data", out var d) ||
                !d.TryGetProperty("rateLimitData", out var rl) ||
                rl.ValueKind != JsonValueKind.Object)
            {
                var gqlErr = TryGetGqlError(doc.RootElement);
                return (502, Err("upstream", gqlErr ?? "Unexpected Warcraft Logs response."));
            }

            var body = JsonSerializer.Serialize(new
            {
                limitPerHour = GetLong(rl, "limitPerHour"),
                pointsSpentThisHour = GetDouble(rl, "pointsSpentThisHour"),
                pointsResetIn = GetLong(rl, "pointsResetIn")
            });
            return (200, body);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Warcraft Logs rate-limit fetch failed");
            return (502, Err("upstream fetch failed", ex.Message));
        }
    }

    private async Task<string?> GetTokenAsync()
    {
        var now = NowUnix();
        if (_token is not null && _tokenExpUnix - 60 > now) return _token;
        if (string.IsNullOrWhiteSpace(_opt.ClientId) || string.IsNullOrWhiteSpace(_opt.ClientSecret)) return null;

        try
        {
            var http = _httpFactory.CreateClient();
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
            _log.LogWarning(ex, "Warcraft Logs token request failed");
            return null;
        }
    }

    private static bool TryGetReportsNode(JsonElement root, out JsonElement node)
    {
        node = default;
        if (root.TryGetProperty("data", out var d) &&
            d.TryGetProperty("reportData", out var rd) &&
            rd.TryGetProperty("reports", out var reports) &&
            reports.ValueKind == JsonValueKind.Object)
        {
            node = reports;
            return true;
        }
        return false;
    }

    private static string? TryGetGqlError(JsonElement root) =>
        root.TryGetProperty("errors", out var errs) &&
        errs.ValueKind == JsonValueKind.Array && errs.GetArrayLength() > 0 &&
        errs[0].TryGetProperty("message", out var m) ? m.GetString() : null;

    private static string GetString(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static long GetLong(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    private static double GetDouble(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) ? n : 0;

    private static string GetNestedName(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Object ? GetString(v, "name") : "";

    private static string Err(string error, string detail) =>
        JsonSerializer.Serialize(new { error, detail });
}
