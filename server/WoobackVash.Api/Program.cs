using Microsoft.EntityFrameworkCore;
using Npgsql;
using WoobackVash.Api.Data;

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

// Readiness: reports whether the DB is configured and reachable.
app.MapGet("/readyz", async (IServiceProvider sp) =>
{
    if (!haveDb) return Results.Ok(new { db = "not_configured" });
    try
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var canConnect = await db.Database.CanConnectAsync();
        return Results.Ok(new { db = canConnect ? "ok" : "unreachable" });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { db = "error", detail = ex.Message });
    }
});

app.MapGet("/", () => Results.Ok(new { service = "wooback-vash-api", phase = 0 }));

app.Run();

// Converts postgres://user:pass@host:port/db[?params] into an Npgsql keyword
// connection string. SSL is required by most managed Postgres (Fly, Supabase).
static string ConnectionStringFromUrl(string url)
{
    var uri = new Uri(url);
    var userInfo = uri.UserInfo.Split(':', 2);
    var csb = new NpgsqlConnectionStringBuilder
    {
        Host = uri.Host,
        Port = uri.IsDefaultPort ? 5432 : uri.Port,
        Username = Uri.UnescapeDataString(userInfo[0]),
        Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "",
        Database = uri.AbsolutePath.TrimStart('/'),
        SslMode = SslMode.Prefer,
        TrustServerCertificate = true
    };
    return csb.ConnectionString;
}

// Exposed so integration tests / EF tooling can reference the entry assembly.
public partial class Program { }
