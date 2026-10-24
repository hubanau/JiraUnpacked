using JiraUnpacked.Domain.Abstractions;
using static JiraUnpacked.Tests.Reporting.JqlTestKit;

namespace JiraUnpacked.Tests.Reporting;

/// <summary>Date conditions as JQL, the translator's clock pinned to Thursday 15 Oct 2026, noon UTC.</summary>
public sealed class JqlDateTests
{
    [Fact]
    public void Comparisons_write_utc_dates_with_a_time_only_when_there_is_one()
    {
        Assert.Equal("created < \"2026-09-01\"", Where(C("createdAt", FilterOperator.Lt, "2026-09-01")));
        Assert.Equal("created >= \"2026-09-01 10:30\"", Where(C("createdAt", FilterOperator.Gte, "2026-09-01T10:30:00Z")));
        Assert.Equal("updated <= \"2026-09-01 08:30\"", Where(C("updatedAt", FilterOperator.Lte, "2026-09-01T10:30:00+02:00")));
        Assert.Equal("updated > \"2026-09-01\"", Where(C("updatedAt", FilterOperator.Gt, "2026-09-01")));
    }

    [Fact]
    public void Between_runs_through_a_date_only_end_and_either_bound_may_be_open()
    {
        Assert.Equal("(created >= \"2026-09-01\" AND created < \"2026-10-01\")",
            Where(L("createdAt", FilterOperator.Between, "2026-09-01", "2026-09-30")));
        Assert.Equal("created >= \"2026-09-01 08:00\"", Where(L("createdAt", FilterOperator.Between, "2026-09-01T08:00:00Z", null)));
        Assert.Equal("created <= \"2026-09-30 18:00\"", Where(L("createdAt", FilterOperator.Between, null, "2026-09-30T18:00:00Z")));
        // Through the last day a date can hold excludes nothing: every issue, as the list reads it.
        var open = Translate(All(L("createdAt", FilterOperator.Between, null, "9999-12-31")));
        Assert.Equal("created is not EMPTY", Where(open));
        Assert.Empty(open.Warnings);   // no date is written, so nothing can shift
    }

    private const string ZoneWarning =
        "Dates are written in UTC; Jira reads them in your Jira profile's time zone, so day boundaries can shift by that offset.";

    [Fact]
    public void Every_written_date_says_once_that_jira_reads_it_in_the_users_time_zone()
    {
        // Comparisons and Between, several of them: one warning.
        var t = Translate(All(
            C("createdAt", FilterOperator.Gte, "2026-09-01"),
            C("updatedAt", FilterOperator.Lt, "2026-09-01T10:30:00Z"),
            L("createdAt", FilterOperator.Between, "2026-09-01", "2026-09-30")));
        Assert.Equal(new[] { ZoneWarning }, t.Warnings);

        // A one-sided Between writes a date too.
        Assert.Equal(new[] { ZoneWarning },
            Translate(All(L("updatedAt", FilterOperator.Between, null, "2026-09-30T18:00:00Z"))).Warnings);

        // History windows with dates: DURING, AFTER and BEFORE.
        foreach (var during in new object[]
                 {
                     new { kind = "between", from = "2026-09-01", to = "2026-09-30" },
                     new { kind = "between", from = "2026-09-01", to = (string?)null },
                     new { kind = "between", from = (string?)null, to = "2026-09-30" },
                 })
        {
            var h = Translate(All(C("history", FilterOperator.Changed, new { historyField = "status", during })));
            Assert.Equal(new[] { ZoneWarning }, h.Warnings);
        }

        // Relative offsets and Jira's own period functions write no date: no such warning.
        Assert.DoesNotContain(ZoneWarning, Translate(All(
            C("createdAt", FilterOperator.InLast, new { amount = 14, unit = "days" }),
            C("createdAt", FilterOperator.InPeriod, new { period = "thisWeek" }))).Warnings);
    }

    [Fact]
    public void In_the_last_and_more_than_ago_are_relative_offsets()
    {
        Assert.Equal("created >= -14d", Where(C("createdAt", FilterOperator.InLast, new { amount = 14, unit = "days" })));
        Assert.Equal("created >= -3h", Where(C("createdAt", FilterOperator.InLast, new { amount = 3, unit = "hours" })));
        Assert.Equal("updated >= -2w", Where(C("updatedAt", FilterOperator.InLast, new { amount = 2, unit = "weeks" })));
        Assert.Equal("updated < -14d", Where(C("updatedAt", FilterOperator.NotInLast, new { amount = 14, unit = "days" })));
        Assert.Empty(Translate(All(C("createdAt", FilterOperator.InLast, new { amount = 14, unit = "days" }))).Warnings);
    }

    [Fact]
    public void Months_are_approximated_as_thirty_days_and_say_so()
    {
        var t = Translate(All(C("createdAt", FilterOperator.InLast, new { amount = 2, unit = "months" })));
        Assert.Equal("created >= -60d", Where(t));
        Assert.Equal(new[] { "JQL has no month unit for relative dates, so months are approximated as 30 days." }, t.Warnings);
    }

    [Fact]
    public void Calendar_periods_use_jiras_start_and_end_functions_with_one_warning()
    {
        var t = Translate(All(
            C("createdAt", FilterOperator.InPeriod, new { period = "thisWeek", timeZone = "Europe/Warsaw" }),
            C("updatedAt", FilterOperator.InPeriod, new { period = "lastMonth" })));
        Assert.Equal(
            "((created >= startOfWeek() AND created <= endOfWeek()) AND (updated >= startOfMonth(-1) AND updated <= endOfMonth(-1)))",
            Where(t));
        Assert.Contains("Jira user's own time zone", Assert.Single(t.Warnings));

        Assert.Equal("(created >= startOfDay() AND created <= endOfDay())",
            Where(C("createdAt", FilterOperator.InPeriod, new { period = "today" })));
        Assert.Equal("(created >= startOfDay(-1) AND created <= endOfDay(-1))",
            Where(C("createdAt", FilterOperator.InPeriod, new { period = "yesterday" })));
        Assert.Equal("(created >= startOfYear(-1) AND created <= endOfYear(-1))",
            Where(C("createdAt", FilterOperator.InPeriod, new { period = "lastYear" })));
    }

    [Fact]
    public void Quarters_are_fixed_dates_from_the_server_clock_in_the_conditions_zone()
    {
        var t = Translate(All(C("createdAt", FilterOperator.InPeriod, new { period = "lastQuarter" })));
        Assert.Equal("(created >= \"2026-07-01\" AND created < \"2026-10-01\")", Where(t));
        Assert.Equal(new[]
        {
            "JQL has no quarter functions, so last quarter is written as fixed dates from \"2026-07-01\" to \"2026-10-01\": the JQL will not move with the calendar.",
            ZoneWarning,
        }, t.Warnings);

        // Warsaw is UTC+2 on 1 Oct and UTC+1 on 1 Jan.
        Assert.Equal("(created >= \"2026-09-30 22:00\" AND created < \"2026-12-31 23:00\")",
            Where(C("createdAt", FilterOperator.InPeriod, new { period = "thisQuarter", timeZone = "Europe/Warsaw" })));
    }

    [Fact]
    public void Seconds_are_truncated_because_jira_compares_at_minute_granularity()
    {
        Assert.Equal("created >= \"2026-09-01 10:30\"", Where(C("createdAt", FilterOperator.Gte, "2026-09-01T10:30:59Z")));
        Assert.Equal("created < \"2026-09-01 00:00\"", Where(C("createdAt", FilterOperator.Lt, "2026-09-01T00:00:30Z")));
    }

    [Fact]
    public void Ranges_are_read_by_the_filters_own_reader()
    {
        // Same inputs the filter accepts or refuses: a reversed or non-date range is left out, never read as open.
        var reversed = Translate(All(L("createdAt", FilterOperator.Between, "2026-10-01", "2026-09-01")));
        Assert.Equal("project = \"ACME\" ORDER BY updated DESC, key ASC", reversed.Jql);
        Assert.Contains("its dates are not valid", Assert.Single(reversed.Warnings));
        Assert.Equal("project = \"ACME\" ORDER BY updated DESC, key ASC",
            Translate(All(L("createdAt", FilterOperator.Between, "garbage", null))).Jql);
        // "from 10:00 on the 30th to the 30th" is not reversed: judged against the effective end.
        Assert.Equal("(created >= \"2026-09-30 10:00\" AND created < \"2026-10-01\")",
            Where(L("createdAt", FilterOperator.Between, "2026-09-30T10:00:00Z", "2026-09-30")));
    }

    [Fact]
    public void A_time_zone_that_is_not_known_is_named_as_the_reason()
    {
        var t = Translate(All(C("createdAt", FilterOperator.InPeriod, new { period = "thisWeek", timeZone = "Mars/Olympus" })));
        Assert.Equal("project = \"ACME\" ORDER BY updated DESC, key ASC", t.Jql);
        var warning = Assert.Single(t.Warnings);
        Assert.Contains("its time zone is not known", warning);
        Assert.DoesNotContain("no period", warning);
        // A missing period keeps its own reason.
        Assert.Contains("it has no period",
            Assert.Single(Translate(All(C("createdAt", FilterOperator.InPeriod, new { timeZone = "UTC" }))).Warnings));
    }
}
