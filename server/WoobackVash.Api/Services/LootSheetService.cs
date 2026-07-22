using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using WoobackVash.Api.Config;

namespace WoobackVash.Api.Services;

/// <summary>
/// Reads one tab of the guild's Google loot sheet.
///
/// There is no API key and no OAuth here: the doc is shared "anyone with the link
/// → viewer", so both views below answer an anonymous GET. The only reason this
/// lives on the server is CORS — Google sends no Access-Control-Allow-Origin, so
/// the browser cannot fetch either of them directly.
///
/// Two views, because the sheet carries meaning in **cell colour**: every spec
/// token is filled with that class's colour, which is the only thing separating
/// the tokens the guild writes ambiguously ("Resto" is the druid in orange and
/// the shaman in blue; "Holy" the priest in white and the paladin in pink).
///  • <c>html</c> — the embedded view. Keeps the fills, ~140 KB a tab. Preferred.
///  • <c>csv</c>  — the plain export. ~9 KB, no formatting at all; the fallback
///                  for the day Google changes the embed, where the page still
///                  works and only the colour disambiguation is lost.
///
/// Each (tab, view) is cached for <see cref="LootSheetOptions.CacheTtlSeconds"/>:
/// the loot-prio page re-fetches on every build, and the sheet is edited between
/// raids rather than during one.
/// </summary>
public class LootSheetService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly LootSheetOptions _opt;
    private readonly ILogger<LootSheetService> _log;

    private record Entry(string Body, DateTimeOffset FetchedAt);
    private readonly ConcurrentDictionary<string, Entry> _cache = new();

    public LootSheetService(
        IHttpClientFactory httpFactory,
        IOptions<LootSheetOptions> opt,
        ILogger<LootSheetService> log)
    {
        _httpFactory = httpFactory;
        _opt = opt.Value;
        _log = log;
    }

    /// <summary>Returns (httpStatus, body, error). On success <paramref name="error"/>
    /// is null; on failure the body is null. <paramref name="gid"/> must already be
    /// validated as digits by the caller — it is interpolated into the URL.
    /// <paramref name="html"/> picks the embedded view over the CSV export.</summary>
    public async Task<(int Status, string? Body, string? Error)> GetTabAsync(string gid, bool html)
    {
        if (string.IsNullOrWhiteSpace(_opt.DocId))
            return (501, null, "The loot sheet document id is not set on the server yet.");

        var key = (html ? "html:" : "csv:") + gid;
        if (_cache.TryGetValue(key, out var hit) &&
            (DateTimeOffset.UtcNow - hit.FetchedAt).TotalSeconds < _opt.CacheTtlSeconds)
            return (200, hit.Body, null);

        var baseUrl = $"{_opt.ExportBase.TrimEnd('/')}/{_opt.DocId}";
        var url = html
            ? $"{baseUrl}/htmlembed/sheet?gid={gid}"
            : $"{baseUrl}/export?format=csv&gid={gid}";

        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(_opt.RequestTimeoutSeconds);

        HttpResponseMessage r;
        try
        {
            r = await http.GetAsync(url);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _log.LogWarning(ex, "Loot sheet fetch failed/timed out for gid {Gid}", gid);
            // A stale copy beats a spinning page mid-raid.
            if (hit is not null) return (200, hit.Body, null);
            return (504, null, "Google Sheets is not responding right now. Try again in a minute.");
        }

        var body = await r.Content.ReadAsStringAsync();
        var contentType = r.Content.Headers.ContentType?.MediaType ?? "";

        if (!r.IsSuccessStatusCode)
        {
            if (hit is not null) return (200, hit.Body, null);
            return (502, null, $"Google Sheets returned {(int)r.StatusCode} for that tab.");
        }

        // A doc that stopped being link-shared answers with a sign-in page rather
        // than an HTTP error, so the content type is what catches the share setting
        // slipping. The embedded view is HTML either way, so there it takes a look
        // at the body: the real one is a table, the sign-in page is not.
        var wrongShape = html
            ? !body.Contains("<table", StringComparison.OrdinalIgnoreCase)
            : !contentType.Contains("csv", StringComparison.OrdinalIgnoreCase);

        if (wrongShape)
        {
            if (hit is not null) return (200, hit.Body, null);
            return (502, null,
                "Google didn't return the sheet — its General access must stay " +
                "\"Anyone with the link → Viewer\".");
        }

        _cache[key] = new Entry(body, DateTimeOffset.UtcNow);
        return (200, body, null);
    }
}
