namespace WoobackVash.Api.Config;

/// <summary>
/// Session-token signing config. Secret comes from env / user-secrets. Reuse the
/// SAME value the old Worker used (SESSION_SECRET) so tokens minted before and
/// after the cutover both verify — nobody gets logged out.
/// </summary>
public class SessionSigningOptions
{
    public const string SectionName = "Session";

    public string Secret { get; set; } = "";

    /// <summary>Session lifetime in seconds (default 7d). Renewals extend by this
    /// much again, so it is really "how long an idle session survives".</summary>
    public int TtlSeconds { get; set; } = 7 * 24 * 60 * 60;

    /// <summary>Hard cap on how long a session can be kept alive by renewal before
    /// a fresh Discord sign-in is required (default 30d). Discord roles are only
    /// re-read at login, so this bounds how stale <c>officer</c> can get.</summary>
    public int MaxLifetimeSeconds { get; set; } = 30 * 24 * 60 * 60;
}
