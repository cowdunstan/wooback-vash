using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using WoobackVash.Api.Auth;
using WoobackVash.Api.Config;
using WoobackVash.Api.Services;

namespace WoobackVash.Api.Proxy;

/// <summary>
/// The gated proxies ported from raidhelper-proxy.worker.js:
///  • /v4/*          Raid-Helper API — officer session required; the RH token is
///                   attached server-side so it never reaches the browser.
///  • /wcl/reports   Warcraft Logs report list — any signed-in tier (logs are
///                   public). ?fresh=1 forces a live refresh (officers only).
///  • /wcl/ratelimit Warcraft Logs points budget — officer-only diagnostics.
///  • /sheet/loot    One tab of the guild Google loot sheet — officer-only. Not a
///                   credential proxy (the doc is link-shared); it exists because
///                   Google sends no CORS header. ?format=html keeps the cell
///                   colours the sheet encodes class in, ?format=csv does not.
/// </summary>
public static class ProxyEndpoints
{
    public static void MapProxyEndpoints(this IEndpointRouteBuilder app)
    {
        // Raid-Helper proxy. The frontend only ever calls /v4/*, so scope the
        // catch-all to that prefix (keeps /healthz, /auth/*, /wcl/* out of it).
        app.MapMethods("/v4/{**rest}", new[] { "GET" }, async (
            HttpContext ctx,
            SessionTokenService tokens,
            IOptions<RaidHelperOptions> opt,
            IHttpClientFactory httpFactory) =>
        {
            var session = ctx.GetSession(tokens);
            if (session is null)
                return Results.Json(new { error = "unauthorized", detail = "Sign-in required." }, statusCode: 401);
            if (!session.Officer)
                return Results.Json(new { error = "forbidden", detail = "Officer access required." }, statusCode: 403);

            var o = opt.Value;
            var target = o.ApiBase.TrimEnd('/') + ctx.Request.Path + ctx.Request.QueryString;

            var http = httpFactory.CreateClient();
            using var req = new HttpRequestMessage(HttpMethod.Get, target);
            // The RH token is sent verbatim (it carries its own scheme, like the
            // Worker's fwd['Authorization'] = env.RH_TOKEN) — bypass validation.
            if (!string.IsNullOrEmpty(o.Token))
                req.Headers.TryAddWithoutValidation("Authorization", o.Token);

            HttpResponseMessage upstream;
            try { upstream = await http.SendAsync(req); }
            catch (Exception err)
            {
                return Results.Json(new { error = "upstream fetch failed", detail = err.Message }, statusCode: 502);
            }

            var body = await upstream.Content.ReadAsStringAsync();
            var contentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/json";
            return Results.Text(body, contentType, statusCode: (int)upstream.StatusCode);
        });

        // Warcraft Logs report list — any valid session is enough (public data).
        app.MapGet("/wcl/reports", async (
            HttpContext ctx,
            SessionTokenService tokens,
            WarcraftLogsService wcl) =>
        {
            var session = ctx.GetSession(tokens);
            if (session is null)
                return Results.Json(new { error = "unauthorized", detail = "Sign-in required." }, statusCode: 401);

            // A forced refresh bypasses the cache TTL and hits Warcraft Logs live.
            // Only officers may trigger it — otherwise anyone could spend the points
            // budget by hitting ?fresh=1 directly, which is what the TTL guards.
            var force = session.Officer &&
                        ctx.Request.Query.TryGetValue("fresh", out var f) &&
                        (f == "1" || f == "true");

            var (status, body) = await wcl.GetReportsAsync(force);
            return Results.Text(body, "application/json", statusCode: status);
        });

        // Warcraft Logs points budget — officer-only diagnostics. Each call hits
        // WCL live and costs a point or two, so it is not open to every tier.
        app.MapGet("/wcl/ratelimit", async (
            HttpContext ctx,
            SessionTokenService tokens,
            WarcraftLogsService wcl) =>
        {
            var session = ctx.GetSession(tokens);
            if (session is null)
                return Results.Json(new { error = "unauthorized", detail = "Sign-in required." }, statusCode: 401);
            if (!session.Officer)
                return Results.Json(new { error = "forbidden", detail = "Officer access required." }, statusCode: 403);

            var (status, body) = await wcl.GetRateLimitAsync();
            return Results.Text(body, "application/json", statusCode: status);
        });

        // One tab of the guild loot sheet — officer-only, because it feeds the
        // loot-prio page, which is officer work. The document id lives in config
        // and is never client-supplied; only the tab (gid) is, and it must be
        // digits, so the URL this builds can't be steered anywhere else.
        //
        // ?format=html (the default) returns the embedded view, which keeps the
        // cell fills the sheet uses to say which class a spec token means;
        // ?format=csv returns the plain export, the fallback that costs only that
        // colour information.
        app.MapGet("/sheet/loot", async (
            HttpContext ctx,
            SessionTokenService tokens,
            LootSheetService sheet) =>
        {
            var (_, error) = ctx.RequireOfficer(tokens);
            if (error is not null) return error;

            var gid = ctx.Request.Query["gid"].ToString();
            if (string.IsNullOrEmpty(gid) || !gid.All(char.IsAsciiDigit))
                return Results.Json(new { error = "bad_request", detail = "A numeric gid is required." },
                                    statusCode: 400);

            var format = ctx.Request.Query["format"].ToString();
            if (format.Length > 0 && format != "html" && format != "csv")
                return Results.Json(new { error = "bad_request", detail = "format must be html or csv." },
                                    statusCode: 400);
            var html = format != "csv";

            var (status, body, err) = await sheet.GetTabAsync(gid, html);
            if (body is null)
                return Results.Json(new { error = "upstream", detail = err }, statusCode: status);
            return Results.Text(body, html ? "text/html" : "text/csv", statusCode: status);
        });
    }
}
