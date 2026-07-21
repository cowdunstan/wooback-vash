namespace WoobackVash.Api.Config;

/// <summary>
/// Blizzard Game Data API config, used for the guild-roster sync. Credentials are
/// secrets (env / user-secrets); the guild identity mirrors the Warcraft Logs one —
/// wooback on Dreamscythe (US). Dreamscythe is a Classic Anniversary realm, which has
/// its own namespace (…-classicann-…): neither retail's (profile-us) nor Classic Era's
/// (profile-classic1x-us) can see the realm at all — they 404 on the guild.
/// </summary>
public class BlizzardOptions
{
    public const string SectionName = "Blizzard";

    public string OAuthUrl { get; set; } = "https://oauth.battle.net/token";
    public string ApiHost { get; set; } = "https://us.api.blizzard.com";
    public string Namespace { get; set; } = "profile-classicann-us";
    public string Locale { get; set; } = "en_US";

    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";

    /// <summary>Realm and guild slugs as they appear in Blizzard API paths (lowercase,
    /// dash-separated).</summary>
    public string RealmSlug { get; set; } = "dreamscythe";
    public string GuildSlug { get; set; } = "wooback";

    /// <summary>Display name stored on characters found on the roster.</summary>
    public string GuildName { get; set; } = "wooback";

    /// <summary>How long a fetched roster stays fresh (seconds). The roster changes a
    /// handful of times a week; officers can force a refresh past this. Default 15 min.</summary>
    public int CacheTtlSeconds { get; set; } = 900;

    /// <summary>Per-request timeout (seconds) so a stalled upstream can't hang the sync.</summary>
    public int RequestTimeoutSeconds { get; set; } = 12;

    public string RosterUrl =>
        $"{ApiHost.TrimEnd('/')}/data/wow/guild/{RealmSlug}/{GuildSlug}/roster" +
        $"?namespace={Namespace}&locale={Locale}";
}
