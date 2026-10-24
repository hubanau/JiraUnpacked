using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Infrastructure.Reporting.Jql;
using static JiraUnpacked.Tests.Reporting.JqlTestKit;

namespace JiraUnpacked.Tests.Reporting;

/// <summary>History conditions as JQL's WAS and CHANGED, the clock pinned to 15 Oct 2026.</summary>
public sealed class JqlHistoryTests
{
    private static readonly JqlLookup People = JqlLookup.Empty with
    {
        AccountIds = Ids(("Alice", new[] { "acc-alice" }), ("Bob", new[] { "acc-bob" })),
    };

    private static FilterCondition H(FilterOperator op, object value) => C("history", op, value);

    [Fact]
    public void Was_names_the_values_and_empty()
    {
        Assert.Equal("status WAS \"Blocked\"",
            Where(H(FilterOperator.Was, new { historyField = "status", values = new[] { "Blocked" } })));
        Assert.Equal("status WAS IN (\"Blocked\", \"On hold\")",
            Where(H(FilterOperator.Was, new { historyField = "status", values = new[] { "Blocked", "On hold" } })));
        Assert.Equal("(status WAS \"Blocked\" OR status WAS EMPTY)",
            Where(H(FilterOperator.Was, new { historyField = "status", values = new string?[] { null, "Blocked" } })));
        Assert.Equal("fixVersion WAS \"1.0\"",
            Where(H(FilterOperator.Was, new { historyField = "Fix Version", values = new[] { "1.0" } })));
        Assert.Equal("assignee WAS \"acc-alice\"",
            Where(H(FilterOperator.Was, new { historyField = "assignee", values = new[] { "Alice" } }), People));
    }

    [Fact]
    public void A_person_with_no_account_narrows_the_history_condition_and_alone_leaves_it_out()
    {
        var t = Translate(All(H(FilterOperator.Was, new { historyField = "assignee", values = new[] { "Alice", "Ghost" } })), People);
        Assert.Equal("assignee WAS \"acc-alice\"", Where(t));
        var warning = Assert.Single(t.Warnings);
        Assert.Contains("'Ghost'", warning);
        Assert.Contains("narrower", warning);

        var u = Translate(All(C("status", FilterOperator.Eq, "Done"),
            H(FilterOperator.Was, new { historyField = "assignee", values = new[] { "Ghost" } })), People);
        Assert.Equal("status = \"Done\"", Where(u));
        Assert.StartsWith("Left out \"history Was of assignee\"", Assert.Single(u.Warnings));
    }

    [Fact]
    public void Changed_takes_from_to_and_by()
    {
        Assert.Equal("status CHANGED", Where(H(FilterOperator.Changed, new { historyField = "status" })));
        Assert.Equal("status CHANGED FROM \"To Do\" TO \"Done\" BY \"acc-bob\"",
            Where(H(FilterOperator.Changed, new { historyField = "status", from = new[] { "To Do" }, to = new[] { "Done" }, by = new[] { "Bob" } }), People));
        Assert.Equal("status CHANGED TO EMPTY",
            Where(H(FilterOperator.Changed, new { historyField = "status", to = new string?[] { null } })));
        Assert.Equal("assignee CHANGED FROM \"acc-alice\"",
            Where(H(FilterOperator.Changed, new { historyField = "assignee", from = new[] { "Alice" } }), People));
    }

    [Fact]
    public void Lists_in_changed_become_their_combinations_up_to_twenty()
    {
        Assert.Equal("(status CHANGED FROM \"A\" TO \"Done\" OR status CHANGED FROM \"B\" TO \"Done\")",
            Where(H(FilterOperator.Changed, new { historyField = "status", from = new[] { "A", "B" }, to = new[] { "Done" } })));

        var five = new[] { "a", "b", "c", "d", "e" };
        var t = Translate(All(C("status", FilterOperator.Eq, "Done"),
            H(FilterOperator.Changed, new { historyField = "status", from = five, to = five })));
        Assert.Equal("status = \"Done\"", Where(t));
        Assert.Contains("it needs 25 CHANGED clauses", Assert.Single(t.Warnings));
    }

    [Fact]
    public void An_author_with_no_account_leaves_changed_out()
    {
        var t = Translate(All(C("status", FilterOperator.Eq, "Done"),
            H(FilterOperator.Changed, new { historyField = "status", by = new[] { "Ghost" } })), People);
        Assert.Equal("status = \"Done\"", Where(t));
        Assert.Contains("(no Jira account in this project's data has those names)", Assert.Single(t.Warnings));
    }

    [Fact]
    public void Windows_become_after_during_and_before()
    {
        string W(object during) => Where(H(FilterOperator.Changed, new { historyField = "status", during }));
        Assert.Equal("status CHANGED AFTER -30d", W(new { kind = "inLast", amount = 30, unit = "days" }));
        Assert.Equal("status CHANGED DURING (startOfMonth(-1), endOfMonth(-1))", W(new { kind = "inPeriod", period = "lastMonth" }));
        Assert.Equal("status CHANGED DURING (\"2026-07-01\", \"2026-10-01\")", W(new { kind = "inPeriod", period = "lastQuarter" }));
        Assert.Equal("status CHANGED DURING (\"2026-09-01\", \"2026-10-01\")", W(new { kind = "between", from = "2026-09-01", to = "2026-09-30" }));
        Assert.Equal("status CHANGED AFTER \"2026-09-01\"", W(new { kind = "between", from = "2026-09-01", to = (string?)null }));
        Assert.Equal("status CHANGED BEFORE \"2026-10-01\"", W(new { kind = "between", from = (string?)null, to = "2026-09-30" }));

        var months = Translate(All(H(FilterOperator.Changed,
            new { historyField = "status", during = new { kind = "inLast", amount = 1, unit = "months" } })));
        Assert.Equal("status CHANGED AFTER -30d", Where(months));
        Assert.Contains("approximated as 30 days", Assert.Single(months.Warnings));
    }

    [Fact]
    public void Number_of_changes_and_unsupported_fields_are_left_out()
    {
        var count = Translate(Any(C("status", FilterOperator.Eq, "Done"),
            H(FilterOperator.ChangeCount, new { historyField = "Sprint", compare = "Gte", count = 3 })));
        Assert.Equal("status = \"Done\"", Where(count));
        Assert.Equal(
            "Left out \"history ChangeCount of Sprint\" (JQL cannot count changes), so the JQL is narrower than this filter: it can miss issues this list shows.",
            Assert.Single(count.Warnings));

        var sprint = Translate(All(C("status", FilterOperator.Eq, "Done"),
            H(FilterOperator.Was, new { historyField = "Sprint", values = new[] { "PLAT 101" } })));
        Assert.Contains("not 'Sprint'", Assert.Single(sprint.Warnings));
    }

    [Fact]
    public void A_window_the_filter_would_refuse_leaves_the_condition_out_and_says_why()
    {
        string Why(object during)
        {
            var t = Translate(All(C("status", FilterOperator.Eq, "Done"),
                H(FilterOperator.Changed, new { historyField = "status", during })));
            Assert.Equal("status = \"Done\"", Where(t));
            return Assert.Single(t.Warnings);
        }

        Assert.Contains("time zone is not known",
            Why(new { kind = "inPeriod", period = "lastMonth", timeZone = "Mars/Olympus" }));
        Assert.Contains("no period", Why(new { kind = "inPeriod" }));
        Assert.Contains("no amount", Why(new { kind = "inLast", unit = "days" }));
        Assert.Contains("dates are not valid", Why(new { kind = "between", from = "2026-09-30", to = "2026-09-01" }));
        Assert.Contains("dates are not valid", Why(new { kind = "between", from = (string?)null, to = (string?)null }));
        Assert.Contains("cannot be read", Why(new { kind = "sometimes" }));
    }

    [Fact]
    public void A_left_out_clause_leaves_no_window_warning_behind()
    {
        var five = new[] { "a", "b", "c", "d", "e" };
        var t = Translate(All(C("status", FilterOperator.Eq, "Done"),
            H(FilterOperator.Changed, new
            {
                historyField = "status", from = five, to = five,
                during = new { kind = "inLast", amount = 1, unit = "months" },
            })));
        Assert.Equal("status = \"Done\"", Where(t));
        Assert.DoesNotContain(t.Warnings, w => w.Contains("approximated"));
    }

    [Fact]
    public void Was_on_a_field_the_list_reads_only_through_its_changes_says_jql_also_matches_the_current_value()
    {
        var fix = Translate(All(H(FilterOperator.Was, new { historyField = "Fix Version", values = new[] { "1.0" } })));
        Assert.Contains("'Fix Version'", Assert.Single(fix.Warnings));
        Assert.Contains("can match issues this list does not show", fix.Warnings[0]);

        var status = Translate(All(H(FilterOperator.Was, new { historyField = "status", values = new[] { "Blocked" } })));
        Assert.Empty(status.Warnings);
    }

    [Fact]
    public void Changed_with_an_empty_by_or_a_malformed_value_never_writes_invalid_jql()
    {
        var empty = Translate(All(C("status", FilterOperator.Eq, "Done"),
            H(FilterOperator.Changed, new { historyField = "status", by = new string?[] { null } })), People);
        Assert.Equal("status = \"Done\"", Where(empty));

        var noField = Translate(All(C("status", FilterOperator.Eq, "Done"), C("history", FilterOperator.Was, "status")));
        Assert.Equal("status = \"Done\"", Where(noField));
        Assert.Contains("names no field", Assert.Single(noField.Warnings));
    }
}
