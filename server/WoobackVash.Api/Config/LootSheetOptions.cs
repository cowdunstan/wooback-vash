namespace WoobackVash.Api.Config;

/// <summary>
/// The guild's Google loot / BIS sheet. Not a secret: the doc is shared "anyone
/// with the link → viewer", which is what lets the CSV export be read with no
/// credentials at all. The proxy exists only because Google sends no CORS header,
/// so the browser can't fetch the export itself.
///
/// <see cref="DocId"/> is the same document sheet.html embeds — keep the two in
/// step (SHEET_EMBED_URL there).
/// </summary>
public class LootSheetOptions
{
    public const string SectionName = "LootSheet";

    /// <summary>Where a document lives; the per-view path is appended to
    /// <c>{ExportBase}/{DocId}</c> (<c>/htmlembed/sheet</c> or <c>/export</c>).</summary>
    public string ExportBase { get; set; } = "https://docs.google.com/spreadsheets/d";

    /// <summary>Google Sheets document id.</summary>
    public string DocId { get; set; } = "";

    /// <summary>How long a fetched tab stays servable without going back to Google.
    /// The sheet is edited between raids, not during one.</summary>
    public int CacheTtlSeconds { get; set; } = 600;

    public int RequestTimeoutSeconds { get; set; } = 12;
}
