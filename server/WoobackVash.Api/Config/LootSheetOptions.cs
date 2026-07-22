namespace WoobackVash.Api.Config;

/// <summary>
/// The guild's Google loot / BIS sheets. Not a secret: each doc is shared "anyone
/// with the link → viewer", which is what lets the CSV export be read with no
/// credentials at all. The proxy exists only because Google sends no CORS header,
/// so the browser can't fetch the export itself.
///
/// There is one document per phase, because the guild raids two at once — P3
/// (Black Temple, Hyjal) and P2 (SSC, Tempest Keep), which are laid out
/// differently and maintained separately. <see cref="Docs"/> is the same set of
/// documents sheet.html embeds — keep the two in step (SHEET_DOCS there).
/// </summary>
public class LootSheetOptions
{
    public const string SectionName = "LootSheet";

    /// <summary>Where a document lives; the per-view path is appended to
    /// <c>{ExportBase}/{docId}</c> (<c>/htmlembed/sheet</c> or <c>/export</c>).</summary>
    public string ExportBase { get; set; } = "https://docs.google.com/spreadsheets/d";

    /// <summary>Google Sheets document ids, keyed by phase ("p3", "p2"). The keys
    /// are the allow-list <c>/sheet/loot?doc=</c> is checked against, so a caller
    /// can never steer the URL at a document that isn't named here.</summary>
    public Dictionary<string, string> Docs { get; set; } = new();

    /// <summary>How long a fetched tab stays servable without going back to Google.
    /// The sheet is edited between raids, not during one.</summary>
    public int CacheTtlSeconds { get; set; } = 600;

    public int RequestTimeoutSeconds { get; set; } = 12;
}
