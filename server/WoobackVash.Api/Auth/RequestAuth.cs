namespace WoobackVash.Api.Auth;

/// <summary>
/// Shared Bearer-session extraction for gated endpoints, matching the Worker's
/// /^Bearer\s+(.+)$/i gate. Returns the verified payload or null.
/// </summary>
public static class RequestAuth
{
    public static SessionPayload? GetSession(this HttpContext ctx, SessionTokenService tokens)
    {
        var auth = ctx.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(auth)) return null;
        const string prefix = "Bearer ";
        if (!auth.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var token = auth[prefix.Length..].Trim();
        return string.IsNullOrEmpty(token) ? null : tokens.Verify(token);
    }

    /// <summary>Require any valid session. Returns (session, null) or (null, 401).</summary>
    public static (SessionPayload? session, IResult? error) RequireSession(this HttpContext ctx, SessionTokenService tokens)
    {
        var s = ctx.GetSession(tokens);
        return s is null
            ? (null, Results.Json(new { error = "unauthorized", detail = "Sign-in required." }, statusCode: 401))
            : (s, null);
    }

    /// <summary>Require an officer session. Returns (session, null), (null, 401) or (null, 403).</summary>
    public static (SessionPayload? session, IResult? error) RequireOfficer(this HttpContext ctx, SessionTokenService tokens)
    {
        var (s, err) = ctx.RequireSession(tokens);
        if (err is not null) return (null, err);
        return s!.Officer
            ? (s, null)
            : (null, Results.Json(new { error = "forbidden", detail = "Officer access required." }, statusCode: 403));
    }
}
