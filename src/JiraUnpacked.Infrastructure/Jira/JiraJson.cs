using System.Text.Json;

namespace JiraUnpacked.Infrastructure.Jira;

/// <summary>
/// JSON options for everything that reads Jira content — the API responses AND the payloads
/// stored from them, which are re-parsed later by ingest and derivation.
///
/// The whole point is <see cref="MaxDepth"/>. System.Text.Json defaults to 64, and a real
/// project hit it: one issue's Atlassian Document Format body — nested lists inside table cells
/// inside panels — went deeper, and `JsonDocument.Parse` threw
/// "The maximum configured depth of 64 has been exceeded" 31,800 issues into a 42,897-issue sync.
/// It is deterministic, not a resource limit: the same issue killed three separate runs at the
/// same point.
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
}
