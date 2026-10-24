using System.Text.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Infrastructure.Reporting.Filtering;

namespace JiraUnpacked.Infrastructure.Reporting.Jql;

/// <summary>History conditions as WAS and CHANGED, for the fields JQL keeps the history of. JQL cannot count changes.</summary>
public sealed partial class JqlTranslator
{
    /// <summary>
    /// The changelog's field names (as Jira reports them, matched exactly like the filter) that
    /// JQL's WAS and CHANGED accept, and their JQL names.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> HistoryFieldsJql =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["status"] = "status",
            ["assignee"] = "assignee",
            ["reporter"] = "reporter",
            ["priority"] = "priority",
            ["resolution"] = "resolution",
            ["Fix Version"] = "fixVersion",
        };

    /// <summary>History fields whose values are people: JQL names them by account id.</summary>
    private static readonly HashSet<string> PeopleFields = new(StringComparer.Ordinal) { "assignee", "reporter" };

    /// <summary>One CHANGED clause takes one FROM, TO and BY; lists are written as their combinations, at most this many.</summary>
    internal const int MaxChangedClauses = 20;

    private const string NoAccountReason = "no Jira account in this project's data has those names";

    /// <summary>A history list as JQL literals, whether (Empty) was in it, and the people names no account has.</summary>
    private sealed record HistoryList(List<string> Literals, bool WantsEmpty, List<string> Unknown);

    private Clause History(FilterCondition c, Run run)
    {
        if (c.Op == FilterOperator.ChangeCount) return Clause.Out("JQL cannot count changes");
        if (c.Value is not { ValueKind: JsonValueKind.Object } v
            || !v.TryGetProperty("historyField", out var hf) || hf.ValueKind != JsonValueKind.String)
            return Clause.Out("it names no field");
        var name = hf.GetString()!;
        if (!HistoryFieldsJql.TryGetValue(name, out var field))
            return Clause.Out($"JQL keeps the history of status, assignee, reporter, priority, resolution and Fix Version only, not '{name}'");
        return c.Op switch
        {
            FilterOperator.Was => Was(field, name, v, run),
            FilterOperator.Changed => Changed(field, name, v, run),
            _ => Clause.Out($"JQL has no '{c.Op}' for history"),
        };
    }

    /// <summary>
    /// The list under <paramref name="key"/>, or null when absent. People (and every BY) by
    /// account id; anything else by its name. Null or "" is (Empty) — except in BY, which takes authors only.
    /// </summary>
    private static HistoryList? ReadHistoryList(JsonElement v, string key, string historyField, Run run)
    {
        if (!v.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        var names = new List<string>();
        var wantsEmpty = false;
        foreach (var e in arr.EnumerateArray())
        {
            var s = e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            if (string.IsNullOrEmpty(s)) wantsEmpty = key != "by";
            else if (!names.Contains(s)) names.Add(s);
        }
        if (key != "by" && !PeopleFields.Contains(historyField))
            return new HistoryList(names.Select(JqlText.Quote).ToList(), wantsEmpty, new List<string>());

        var literals = new List<string>();
        var unknown = new List<string>();
        foreach (var n in names)
        {
            if (run.Lookup.AccountIds.TryGetValue(n, out var ids) && ids.Count > 0)
            {
                foreach (var id in ids)
                {
                    var l = JqlText.Quote(id);
                    if (!literals.Contains(l)) literals.Add(l);
                }
            }
            else
            {
                unknown.Add(n);
            }
        }
        return new HistoryList(literals, wantsEmpty, unknown);
    }

    /// <summary>
    /// Unlike a current assignee, history matches the changelog's display names — a person with no
    /// account row can still be in it. So dropping a name here does change the meaning: narrower.
    /// </summary>
    private static string UnknownPeople(List<string> unknown) =>
        $"No Jira account in this project's data is named {string.Join(", ", unknown.Select(n => $"'{n}'"))}, so the JQL leaves {(unknown.Count == 1 ? "that name" : "those names")} out of a history condition and is narrower than this filter: it can miss issues this list shows.";

    /// <summary>
    /// Jira's WAS also matches the value an issue has now, which the list counts only for the
    /// fields it keeps on the issue (status, assignee, priority); for the rest it reads recorded changes alone.
    /// </summary>
    private static string CurrentValueWarning(string historyField) =>
        $"Jira's WAS also matches the value an issue has now, even one set when it was created; this list matches '{historyField}' only through its recorded changes, so the JQL can match issues this list does not show.";

    /// <summary>WAS / WAS IN for the values, WAS EMPTY for (Empty), OR-ed.</summary>
    private static Clause Was(string field, string historyField, JsonElement v, Run run)
    {
        var list = ReadHistoryList(v, "values", historyField, run);
        if (list is null) return Clause.Out("it names no value");
        var parts = new List<string>();
        if (list.Literals.Count == 1) parts.Add($"{field} WAS {list.Literals[0]}");
        else if (list.Literals.Count > 1) parts.Add($"{field} WAS IN {JqlText.List(list.Literals)}");
        if (list.WantsEmpty) parts.Add($"{field} WAS EMPTY");
        if (parts.Count == 0) return Clause.Out(list.Unknown.Count > 0 ? NoAccountReason : "it names no value");
        if (list.Unknown.Count > 0) run.Warn(UnknownPeople(list.Unknown));
        if (!IssueFilterTranslator.CurrentValueFields.ContainsKey(historyField)) run.Warn(CurrentValueWarning(historyField));
        return Clause.Of(JqlText.Join(parts, "OR"));
    }

    /// <summary>
    /// CHANGED [FROM x] [TO y] [BY account] [window]. JQL takes one value per FROM, TO and BY, so
    /// lists become their combinations, OR-ed — each still ONE change satisfying all its parts, as
    /// the filter means it — up to <see cref="MaxChangedClauses"/>.
    /// </summary>
    private Clause Changed(string field, string historyField, JsonElement v, Run run)
    {
        var fromList = ReadHistoryList(v, "from", historyField, run);
        var toList = ReadHistoryList(v, "to", historyField, run);
        var byList = ReadHistoryList(v, "by", historyField, run);
        var lists = new[] { fromList, toList, byList };
        // A list given but left with nothing JQL can name would drop that requirement entirely.
        foreach (var list in lists)
        {
            if (list is not null && list.Literals.Count == 0 && !list.WantsEmpty)
                return Clause.Out(list.Unknown.Count > 0 ? NoAccountReason : "a list in it names no value");
        }

        static List<string?> Options(HistoryList? list, string keyword)
        {
            if (list is null) return new List<string?> { null };
            var options = list.Literals.Select(l => (string?)$"{keyword} {l}").ToList();
            if (list.WantsEmpty) options.Add($"{keyword} EMPTY");
            return options;
        }

        var combos = Options(fromList, "FROM")
            .SelectMany(f => Options(toList, "TO"), (f, t) => (f, t))
            .SelectMany(ft => Options(byList, "BY"), (ft, b) => new[] { ft.f, ft.t, b })
            .ToList();
        if (combos.Count > MaxChangedClauses)
            return Clause.Out($"it needs {combos.Count} CHANGED clauses — JQL takes one value per FROM, TO and BY — and at most {MaxChangedClauses} are written");

        // Last, so a clause left out for any reason above never leaves a window's warning behind.
        string? window = null;
        if (v.TryGetProperty("during", out var d) && d.ValueKind != JsonValueKind.Null)
        {
            var (jql, why) = Window(d, run);
            if (jql is null) return Clause.Out(why!);
            window = jql;
        }

        foreach (var list in lists)
        {
            if (list is { Unknown.Count: > 0 }) run.Warn(UnknownPeople(list.Unknown));
        }

        var clauses = combos.Select(parts =>
        {
            var words = new List<string> { $"{field} CHANGED" };
            words.AddRange(parts.OfType<string>());
            if (window is not null) words.Add(window);
            return string.Join(" ", words);
        }).ToList();
        return Clause.Of(JqlText.Join(clauses, "OR"));
    }

    /// <summary>
    /// A `during` as a CHANGED predicate — AFTER a relative offset, DURING a period, or the between
    /// bounds — read by the same helpers as the date conditions; or why it cannot be written.
    /// </summary>
    private (string? Jql, string? Why) Window(JsonElement d, Run run)
    {
        const string unreadable = "its window cannot be read";
        if (d.ValueKind != JsonValueKind.Object
            || !d.TryGetProperty("kind", out var k) || k.ValueKind != JsonValueKind.String)
            return (null, unreadable);
        switch (k.GetString())
        {
            case "inLast":
                return Relative(d, run) is { } offset ? ($"AFTER {offset}", null) : (null, "its window has no amount");
            case "inPeriod":
                return Period(d, run, out var why) is { } p
                    ? ($"DURING ({p.Start}, {p.End})", null)
                    : (null, $"its window: {why}");
            case "between":
                JsonElement? fromEl = d.TryGetProperty("from", out var f) && f.ValueKind != JsonValueKind.Null ? f : null;
                JsonElement? toEl = d.TryGetProperty("to", out var t) && t.ValueKind != JsonValueKind.Null ? t : null;
                // The filter refuses a window with both bounds open; so does this.
                if ((fromEl is null && toEl is null) || ReadRange(fromEl, toEl) is not { } range)
                    return (null, "its window dates are not valid");
                var (lo, hi, _) = range;
                if (lo is { } start && hi is { } end) return ($"DURING ({JqlText.Date(start)}, {JqlText.Date(end)})", null);
                if (lo is { } since) return ($"AFTER {JqlText.Date(since)}", null);
                if (hi is { } until) return ($"BEFORE {JqlText.Date(until)}", null);
                return (null, unreadable);
            default:
                return (null, unreadable);
        }
    }
}
