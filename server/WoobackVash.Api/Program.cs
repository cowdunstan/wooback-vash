using Microsoft.EntityFrameworkCore;
using Npgsql;
using WoobackVash.Api.Api;
using WoobackVash.Api.Auth;
using WoobackVash.Api.Config;
using WoobackVash.Api.Data;
using WoobackVash.Api.Proxy;
using WoobackVash.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// ── Configuration ──────────────────────────────────────────────────────────
// Fly injects the database as a URL (postgres://user:pass@host:5432/db). Npgsql
// wants a keyword connection string, so translate when a URL is present. Falls
// back to the "Default" connection string (appsettings / env) for local dev.
var dbUrl = builder.Configuration["DATABASE_URL"]
            ?? Environment.GetEnvironmentVariable("DATABASE_URL");
var connString = !string.IsNullOrWhiteSpace(dbUrl)
    ? ConnectionStringFromUrl(dbUrl)
    : builder.Configuration.GetConnectionString("Default");

var haveDb = !string.IsNullOrWhiteSpace(connString);
if (haveDb)
{
    builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connString));
}

// Auth: Discord OAuth options + session-token signing + an HttpClient for Discord.
builder.Services.Configure<DiscordOptions>(builder.Configuration.GetSection(DiscordOptions.SectionName));
builder.Services.Configure<SessionSigningOptions>(builder.Configuration.GetSection(SessionSigningOptions.SectionName));
builder.Services.AddSingleton<SessionTokenService>();
builder.Services.AddHttpClient();

// Gated proxies (Phase 2): Raid-Helper + Warcraft Logs.
builder.Services.Configure<RaidHelperOptions>(builder.Configuration.GetSection(RaidHelperOptions.SectionName));
builder.Services.Configure<WarcraftLogsOptions>(builder.Configuration.GetSection(WarcraftLogsOptions.SectionName));
builder.Services.AddSingleton<WarcraftLogsService>();

// CORS: only the app's own origins may call the API from a browser. Kept in sync
// with the origins the old Worker allowed (see raidhelper-proxy.worker.js).
var allowedOrigins = builder.Configuration.GetSection("AllowedOrigins").Get<string[]>()
    ?? new[] { "https://wooback.info", "https://www.wooback.info", "https://cowdunstan.github.io" };
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(allowedOrigins)
    .AllowAnyHeader()
    .WithMethods("GET", "POST", "PUT", "DELETE", "OPTIONS")));

var app = builder.Build();

app.UseCors();

// ── Startup migration (guarded) ────────────────────────────────────────────
// Applies EF Core migrations when a database is configured. Wrapped so the API
// still boots for /healthz before Postgres is provisioned (Phase 0) and before
// any migration exists.
if (haveDb)
{
    using var scope = app.Services.CreateScope();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.Migrate();
        logger.LogInformation("Database migrations applied.");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Database migration skipped/failed (continuing so /healthz stays up).");
    }
}

// ── Routes ─────────────────────────────────────────────────────────────────
// Liveness: never touches the database.
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

// Readiness: reports whether the DB is configured and reachable. Opens a real
// connection so a failure surfaces the underlying reason (host/auth/SSL/timeout)
// rather than a bare "unreachable" — invaluable when wiring up Fly Postgres.
app.MapGet("/readyz", async (IServiceProvider sp) =>
{
    if (!haveDb) return Results.Ok(new { db = "not_configured" });
    try
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.OpenConnectionAsync();
        await db.Database.CloseConnectionAsync();
        return Results.Ok(new { db = "ok" });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { db = "unreachable", detail = ex.GetBaseException().Message });
    }
});

app.MapGet("/", () => Results.Ok(new { service = "wooback-vash-api", phase = 4 }));

// Discord OAuth login/callback (Phase 1).
app.MapAuthEndpoints();

// Gated Raid-Helper + Warcraft Logs proxies (Phase 2).
app.MapProxyEndpoints();

// Persistence: Vash board save/load + identity links (Phase 3).
app.MapBoardEndpoints();
app.MapMembersEndpoints();

// Loot + attendance history (Phase 4).
app.MapRaidLogEndpoints();

app.Run();

// Converts postgres://user:pass@host:port/db[?sslmode=...] into an Npgsql keyword
// connection string. SSL handling: honor an explicit sslmode from the URL (Fly's
// `postgres attach` sets sslmode=disable for the internal .flycast network, since
// that traffic is already encrypted over WireGuard; external managed Postgres set
// require). With no sslmode given, default internal hosts to Disable and everything
// else to Require — matching how these providers actually expect to be reached.
// Getting this wrong yields "unexpected EOF from the transport stream".
static string ConnectionStringFromUrl(string url)
{
    var uri = new Uri(url);
    var userInfo = uri.UserInfo.Split(':', 2);
    var host = uri.Host;
    var isInternal = host.EndsWith(".flycast", StringComparison.OrdinalIgnoreCase)
                     || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase);

    var csb = new NpgsqlConnectionStringBuilder
    {
        Host = host,
        Port = uri.IsDefaultPort ? 5432 : uri.Port,
        Username = Uri.UnescapeDataString(userInfo[0]),
        Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "",
        Database = uri.AbsolutePath.TrimStart('/')
    };

    var sslmode = QueryValue(uri.Query, "sslmode");
    csb.SslMode = sslmode?.ToLowerInvariant() switch
    {
        "disable" => SslMode.Disable,
        "allow" => SslMode.Allow,
        "prefer" => SslMode.Prefer,
        "require" => SslMode.Require,
        "verify-ca" => SslMode.VerifyCA,
        "verify-full" => SslMode.VerifyFull,
        _ => isInternal ? SslMode.Disable : SslMode.Require
    };
    return csb.ConnectionString;
}

// First value for a key in a raw URL query string ("?a=1&b=2"), or null.
static string? QueryValue(string query, string key)
{
    if (string.IsNullOrEmpty(query)) return null;
    foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
    {
        var kv = part.Split('=', 2);
        if (Uri.UnescapeDataString(kv[0]).Equals(key, StringComparison.OrdinalIgnoreCase))
            return kv.Length == 2 ? Uri.UnescapeDataString(kv[1]) : "";
    }
    return null;
}

// Exposed so integration tests / EF tooling can reference the entry assembly.
public partial class Program { }
