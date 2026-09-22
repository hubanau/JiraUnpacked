namespace JiraUnpacked.Infrastructure.Reporting;

/// <summary>
/// RFC 4180 quoting, plus neutralisation of values a spreadsheet would execute.
/// </summary>
public static class CsvWriter
{
    /// <summary>
    /// Characters that make Excel and Sheets treat a cell as a formula rather than text.
    ///
    /// A Jira summary is text a stranger can write, so an export of someone else's issues would
    /// otherwise run their =HYPERLINK(...) on the machine of whoever opens the file. Tab and CR
    /// are here because a leading one can shift the rest of the cell into the next field, which
    /// some parsers then re-interpret.
    /// </summary>
    private static readonly char[] Executable = { '=', '+', '-', '@', '\t', '\r' };

    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";

        // The apostrophe goes on BEFORE quoting, so it lands inside the quotes where the
        // spreadsheet reads it as the cell's first character and treats the rest as text.
        // Quoting alone does not prevent execution — this is a separate defence, not a variant
        // of the same one.
        var escaped = Executable.Contains(value[0]) ? "'" + value : value;

        var needsQuotes = escaped != value
            || escaped.Contains(',') || escaped.Contains('"')
            || escaped.Contains('\n') || escaped.Contains('\r');

        return needsQuotes ? "\"" + escaped.Replace("\"", "\"\"") + "\"" : escaped;
    }

    /// <summary>One CSV record, terminated with \r\n as RFC 4180 specifies.</summary>
    public static string Row(IEnumerable<string?> cells) =>
        string.Join(",", cells.Select(Escape)) + "\r\n";
}
