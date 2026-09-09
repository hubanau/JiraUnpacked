using System.Text.Json;

namespace JiraUnpacked.Infrastructure.Jira;

/// <summary>
/// JSON handling for everything that reads or stores Jira content — the API responses AND the
/// payloads kept from them, which are re-parsed later by ingest and derivation.
///
/// <see cref="MaxDepth"/>: System.Text.Json defaults to 64, and a real project hit it. One
/// issue's Atlassian Document Format body — nested lists inside table cells inside panels — went
/// deeper, so JsonDocument.Parse threw "The maximum configured depth of 64 has been exceeded"
/// 31,800 issues into a 42,897-issue sync. It was deterministic rather than a resource limit:
/// the same document killed three separate runs at the same point.
///
/// Raising it is safe HERE specifically. The depth limit guards against hostile input consuming
/// unbounded resources while being read; this input comes from the tenant's own authenticated
/// Jira and is fully buffered in memory before parsing even starts, so a deeper allowance costs
/// a slightly larger parse stack and nothing else. 512 is far past anything a person authors —
/// roughly 250 levels of nested lists — while still being a bound rather than none.
///
/// Every site that reads Jira JSON must use these. Raising the limit only where the response is
/// parsed would let a document be STORED and then fail identically in derivation, moving the
/// failure rather than fixing it.
/// </summary>
internal static class JiraJson
{
    public const int MaxDepth = 512;

    public static readonly JsonDocumentOptions Document = new() { MaxDepth = MaxDepth };

    public static readonly JsonSerializerOptions Web =
        new(JsonSerializerDefaults.Web) { MaxDepth = MaxDepth };

    /// <summary>
    /// The six characters JSON uses to escape NUL: backslash, u, 0, 0, 0, 0 — NOT the NUL
    /// character itself. Built from the backslash's code point rather than written as a literal,
    /// because a source file containing this escape is one careless rewrite away from holding a
    /// real NUL instead, and that corruption is invisible on screen.
    /// </summary>
    private static readonly string NulEscape = ((char)0x5C) + "u0000";

    /// <summary>
    /// Removes NUL from JSON about to be stored in a jsonb column.
    ///
    /// PostgreSQL cannot hold NUL in json or jsonb. Neither a bug nor configurable: jsonb is
    /// converted to text, and text cannot represent NUL, so Postgres rejects the whole INSERT
    /// with 22P05 "unsupported Unicode escape sequence". Jira accepts a NUL pasted into a text
    /// field and serves it back escaped, so a single such issue anywhere in a project failed the
    /// page it landed on and, through that, the entire sync — a real 42,918-issue run died on
    /// a description reading "Date/Time of Incident: - June 22, 8:53 am" followed by one.
    ///
    /// Dropped rather than replaced: NUL carries no meaning in a text field, and substituting a
    /// visible character would invent content that was never in Jira. Stripping the escape from
    /// the JSON TEXT leaves the surrounding string intact and the document still valid JSON.
    /// Rejecting the issue instead would lose a real issue over an invisible character.
    ///
    /// The common case — no NUL anywhere — allocates nothing.
    /// </summary>
    public static string StripNulls(string json)
    {
        // A raw NUL is not legal in JSON text, but it costs nothing to guard against and would
        // fail in exactly the same way if anything upstream ever emitted one unescaped.
        var hasEscaped = json.Contains(NulEscape, StringComparison.OrdinalIgnoreCase);
        var hasRaw = json.IndexOf((char)0) >= 0;
        if (!hasEscaped && !hasRaw) return json;

        if (hasEscaped)
            json = json.Replace(NulEscape, string.Empty, StringComparison.OrdinalIgnoreCase);
        if (hasRaw)
            json = json.Replace(((char)0).ToString(), string.Empty, StringComparison.Ordinal);
        return json;
    }
}
