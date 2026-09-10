using System.Text;
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
    /// A single backslash, by code point rather than as a literal. A source file holding these
    /// sequences is one careless rewrite away from holding the character they describe instead,
    /// and that corruption is invisible on screen — which has already happened once here.
    /// </summary>
    private const char Backslash = (char)0x5C;

    /// <summary>
    /// Makes a Jira payload storable in a jsonb column.
    ///
    /// One method rather than a call site per hazard, because the hazards keep arriving: Jira
    /// accepts things in its text fields that PostgreSQL cannot represent, and each one has failed
    /// an entire 40,000-issue sync over a single issue. Everything a payload must survive belongs
    /// here, applied at every point a payload becomes a row.
    /// </summary>
    public static string SanitiseForStorage(string json) =>
        RepairEscapes(StripNulls(json));

    /// <summary>
    /// Repairs the escape sequences PostgreSQL refuses but System.Text.Json lets through.
    ///
    /// A code point above U+FFFF is written in JSON as a SURROGATE PAIR — two escapes that mean
    /// one character only together. An escape from the surrogate range without its partner is not
    /// a character at all, and PostgreSQL rejects the whole document with 22P02 "invalid input
    /// syntax for type json / Unicode low surrogate must follow a high surrogate".
    ///
    /// It arrives from binary pasted into a text field: a real sync died on an issue holding a raw
    /// PNG whose bytes had been mangled into text, leaving orphaned halves behind. .NET never
    /// surfaces this on its own — encoding a lone surrogate to UTF-8 quietly substitutes U+FFFD —
    /// so the ESCAPED form travels intact all the way to Postgres, which is stricter.
    ///
    /// Dropped rather than replaced with U+FFFD, which is the conventional marker for exactly
    /// this. U+FFFD would be defensible, but the surrounding content is already the wreckage of
    /// binary-as-text and a visible glyph would be content Jira never sent — the same reasoning
    /// that drops NUL. VALID PAIRS MUST SURVIVE: an emoji in a summary is ordinary and correct.
    ///
    /// The other half of the job: a backslash followed by anything JSON does not define as an
    /// escape. GetRawText hands back the ORIGINAL bytes without validating them, so such a
    /// payload parses perfectly and then fails at the INSERT with 22P02, DETAIL "Escape sequence
    /// is invalid" — which is how a raw PNG in an issue's text field killed a project's first
    /// sync at exactly one page, three separate times.
    /// </summary>
    public static string RepairEscapes(string json)
    {
        // Nothing to repair without a backslash, and most payloads are dominated by plain text.
        if (json.IndexOf(Backslash) < 0) return json;

        var sb = new StringBuilder(json.Length);
        var i = 0;

        while (i < json.Length)
        {
            if (json[i] != Backslash)
            {
                sb.Append(json[i]);
                i++;
                continue;
            }

            // A trailing backslash cannot begin anything. Dropping it is the only repair.
            if (i + 1 >= json.Length) { i++; continue; }

            var next = json[i + 1];

            // The two-character escapes JSON defines, consumed ATOMICALLY — which is what stops
            // an escaped backslash from being misread as the start of the escape after it.
            if (next is '"' or '\\' or '/' or 'b' or 'f' or 'n' or 'r' or 't')
            {
                sb.Append(json[i]).Append(next);
                i += 2;
                continue;
            }

            if (next == 'u' && TryReadUnicodeEscape(json, i, out var value))
            {
                // An escaped NUL, removed HERE rather than by a substring replace over the whole
                // text. That replace is what broke this: in valid JSON containing an escaped
                // backslash followed by the literal characters u0000, it matched from the SECOND
                // backslash, cut six characters out, and left a lone backslash in front of the
                // next character — an invalid escape, which Postgres then refused. Reaching it
                // through the scanner means only a real escape is ever touched.
                if (value == 0)
                {
                    i += 6;
                }
                else if (char.IsHighSurrogate((char)value))
                {
                    // Valid only when its low half follows immediately.
                    if (TryReadUnicodeEscape(json, i + 6, out var low) && char.IsLowSurrogate((char)low))
                    {
                        sb.Append(json, i, 12);
                        i += 12;
                    }
                    else
                    {
                        i += 6;   // orphaned high half — drop
                    }
                }
                else if (char.IsLowSurrogate((char)value))
                {
                    i += 6;       // a low half reaches here only unpaired — drop
                }
                else
                {
                    sb.Append(json, i, 6);
                    i += 6;
                }
                continue;
            }

            // Anything else after a backslash is not an escape JSON defines, and PostgreSQL
            // rejects the whole document: 22P02, DETAIL "Escape sequence is invalid". Only the
            // BACKSLASH is dropped; the character after it is ordinary and is copied on the next
            // pass round, so the text keeps everything that was really there. Also covers \u
            // without four hex digits, which is the same defect wearing a longer name.
            i++;
        }

        return sb.ToString();
    }

    /// <summary>Reads a six-character unicode escape at <paramref name="i"/>, or reports there is none.</summary>
    private static bool TryReadUnicodeEscape(string json, int i, out int value)
    {
        value = 0;
        if (i < 0 || i + 5 >= json.Length || json[i] != Backslash || json[i + 1] != 'u') return false;

        var parsed = 0;
        for (var d = 2; d < 6; d++)
        {
            var c = json[i + d];
            var digit = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'f' => c - 'a' + 10,
                >= 'A' and <= 'F' => c - 'A' + 10,
                _ => -1,
            };
            if (digit < 0) return false;
            parsed = (parsed << 4) | digit;
        }

        value = parsed;
        return true;
    }

    /// <summary>
    /// Removes RAW NUL characters — the actual character, never the escape.
    ///
    /// PostgreSQL cannot hold NUL in json or jsonb: jsonb is converted to text, and text cannot
    /// represent NUL, so it rejects the whole INSERT with 22P05 "unsupported Unicode escape
    /// sequence". Jira accepts a NUL pasted into a text field and serves it back, so this is
    /// ordinary data — a real 42,918-issue run died on a description reading
    /// "Date/Time of Incident: - June 22, 8:53 am" followed by one.
    ///
    /// The ESCAPED form is deliberately NOT handled here. It used to be, by a substring replace
    /// over the whole document, and that replace could not tell a real escape from the literal
    /// characters u0000 sitting after an escaped backslash — so it cut six characters out of
    /// valid JSON and left a lone backslash behind, which Postgres refused in turn. Escapes are
    /// only safe to touch from a scanner that knows where each one begins, so
    /// <see cref="RepairEscapes"/> does it.
    ///
    /// Dropped rather than replaced: NUL carries no meaning in a text field, and substituting a
    /// visible character would invent content that was never in Jira. Rejecting the issue instead
    /// would lose a real issue over an invisible character.
    ///
    /// The common case — no NUL anywhere — allocates nothing.
    /// </summary>
    public static string StripNulls(string json) =>
        json.IndexOf((char)0) < 0 ? json : json.Replace(((char)0).ToString(), string.Empty, StringComparison.Ordinal);
}
