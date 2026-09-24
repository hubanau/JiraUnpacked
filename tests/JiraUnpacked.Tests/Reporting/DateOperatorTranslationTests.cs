using System.Globalization;
using System.Text.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Infrastructure.Reporting.Filtering;
using JiraUnpacked.Tests.Security;

namespace JiraUnpacked.Tests.Reporting;

/// <summary>
/// Between / InLast / NotInLast / InPeriod through the translator, its clock pinned to a
/// Wednesday noon. Predicates are compiled and run in memory, like the other translator tests.
/// </summary>
public sealed class DateOperatorTranslationTests
{
    private static readonly DateTime Now = new(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);
    private static readonly IIssueFilterTranslator Sut = new IssueFilterTranslator(new FakeClock(Now));

    private static JsonElement J(object? v) => JsonSerializer.SerializeToElement(v);

    private static Task<IReadOnlyList<Guid>> NoDictionary(string lookup, IReadOnlyList<string> names, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>());

    private static async Task<Func<Issue, bool>> Pred(FilterCondition c)
    {
        var r = await Sut.BuildAsync(new[] { c }, NoDictionary, CancellationToken.None);
        Assert.Null(r.Error);
        return r.Predicate!.Compile();
    }

    private static async Task<string> Error(FilterCondition c)
    {
        var r = await Sut.BuildAsync(new[] { c }, NoDictionary, CancellationToken.None);
        Assert.Null(r.Predicate);
        return Assert.IsType<string>(r.Error);
    }

    private static DateTime U(int y, int mo, int d, int h = 0, int mi = 0, int s = 0) =>
        new(y, mo, d, h, mi, s, DateTimeKind.Utc);

    private static DateTime Day(string yyyyMmDd) =>
        DateTime.ParseExact(yyyyMmDd, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static Issue At(DateTime at) => new()
    {
        Id = Guid.NewGuid(), JiraIssueId = "1", JiraKey = "A-1", JiraCreatedAt = at, JiraUpdatedAt = at,
    };

    private static Issue Pts(decimal? points) => new()
    {
        Id = Guid.NewGuid(), JiraIssueId = "1", JiraKey = "A-1", StoryPoints = points,
        JiraCreatedAt = Now, JiraUpdatedAt = Now,
    };

    private static FilterCondition Last(int amount, string unit, FilterOperator op = FilterOperator.InLast) =>
        new("updatedAt", op, J(new { amount, unit }), null);

    private static FilterCondition Period(string period, string? timeZone = null) =>
        new("updatedAt", FilterOperator.InPeriod,
            timeZone is null ? J(new { period }) : J(new { period, timeZone }), null);

    private static FilterCondition Between(string field, object? from, object? to) =>
        new(field, FilterOperator.Between, null, new List<JsonElement> { J(from), J(to) });

    // ---- InLast / NotInLast ----

    [Theory]
    [InlineData("hours", 5, 2026, 9, 23, 7)]
    [InlineData("days", 14, 2026, 9, 9, 12)]
    [InlineData("weeks", 2, 2026, 9, 9, 12)]
    [InlineData("months", 1, 2026, 8, 23, 12)]
    public async Task InLast_and_NotInLast_split_at_now_minus_the_amount(
        string unit, int amount, int y, int m, int d, int h)
    {
        var boundary = U(y, m, d, h);
        var inLast = await Pred(Last(amount, unit));
        var notInLast = await Pred(Last(amount, unit, FilterOperator.NotInLast));

        Assert.True(inLast(At(boundary)));                     // the boundary itself is "in the last"
        Assert.False(inLast(At(boundary.AddSeconds(-1))));
        Assert.False(notInLast(At(boundary)));
        Assert.True(notInLast(At(boundary.AddSeconds(-1))));   // "more than 14 days ago"
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3651)]
    public async Task InLast_refuses_an_amount_outside_1_to_3650(int amount) =>
        Assert.Contains("amount", await Error(Last(amount, "days")));

    [Fact]
    public async Task InLast_refuses_a_fraction_an_unknown_unit_and_a_value_of_the_wrong_shape()
    {
        Assert.Contains("amount", await Error(new("updatedAt", FilterOperator.InLast, J(new { amount = 1.5, unit = "days" }), null)));
        Assert.Contains("'years'", await Error(Last(2, "years")));
        Assert.Contains("amount", await Error(new("updatedAt", FilterOperator.InLast, J("14 days"), null)));
        Assert.Contains("amount", await Error(new("updatedAt", FilterOperator.NotInLast, null, null)));
    }

    // ---- InPeriod ----

    [Theory]
    [InlineData("today",       "2026-09-23", "2026-09-24")]
    [InlineData("yesterday",   "2026-09-22", "2026-09-23")]
    [InlineData("thisWeek",    "2026-09-21", "2026-09-28")]
    [InlineData("lastWeek",    "2026-09-14", "2026-09-21")]
    [InlineData("thisMonth",   "2026-09-01", "2026-10-01")]
    [InlineData("lastMonth",   "2026-08-01", "2026-09-01")]
    [InlineData("thisQuarter", "2026-07-01", "2026-10-01")]
    [InlineData("lastQuarter", "2026-04-01", "2026-07-01")]
    [InlineData("thisYear",    "2026-01-01", "2027-01-01")]
    [InlineData("lastYear",    "2025-01-01", "2026-01-01")]
    public async Task InPeriod_includes_its_first_instant_and_excludes_the_next_periods(string period, string start, string end)
    {
        var (s, e) = (Day(start), Day(end));
        var pred = await Pred(Period(period));

        Assert.True(pred(At(s)));
        Assert.True(pred(At(e.AddSeconds(-1))));
        Assert.False(pred(At(s.AddSeconds(-1))));
        Assert.False(pred(At(e)));
    }

    [Fact]
    public async Task InPeriod_uses_the_conditions_zone_and_a_missing_or_null_zone_means_utc()
    {
        // Noon UTC on the 23rd. In Warsaw (UTC+2) "today" began at 22:00 UTC on the 22nd.
        var lateOn22ndUtc = U(2026, 9, 22, 22, 30);
        var nullZone = new FilterCondition("updatedAt", FilterOperator.InPeriod,
            J(new { period = "today", timeZone = (string?)null }), null);

        Assert.True((await Pred(Period("today", "Europe/Warsaw")))(At(lateOn22ndUtc)));
        Assert.False((await Pred(Period("today")))(At(lateOn22ndUtc)));
        Assert.False((await Pred(nullZone))(At(lateOn22ndUtc)));
    }

    [Theory]
    [InlineData("Nope/Nowhere")]
    [InlineData("../../etc/passwd")]
    [InlineData("Central European Standard Time")]
    public async Task InPeriod_refuses_a_time_zone_that_is_not_an_iana_id_this_host_has(string zone) =>
        Assert.Contains("Unknown time zone", await Error(Period("today", zone)));

    [Fact]
    public async Task InPeriod_refuses_an_unknown_period_a_missing_one_and_a_zone_that_is_not_a_string()
    {
        Assert.Contains("'fortnight'", await Error(Period("fortnight")));
        Assert.Contains("period", await Error(new("updatedAt", FilterOperator.InPeriod, J(new { timeZone = "UTC" }), null)));
        Assert.Contains("timeZone", await Error(new("updatedAt", FilterOperator.InPeriod, J(new { period = "today", timeZone = 5 }), null)));
    }

    // ---- Between on dates ----

    [Fact]
    public async Task Between_dates_with_a_date_only_end_includes_that_whole_day()
    {
        var pred = await Pred(Between("updatedAt", "2026-09-01", "2026-09-30"));

        Assert.True(pred(At(U(2026, 9, 1))));
        Assert.False(pred(At(U(2026, 8, 31, 23, 59, 59))));
        Assert.True(pred(At(U(2026, 9, 30, 23, 59, 59))));
        Assert.False(pred(At(U(2026, 10, 1))));
    }

    [Fact]
    public async Task Between_dates_with_a_timestamp_end_stops_at_that_instant()
    {
        var pred = await Pred(Between("createdAt", "2026-09-01", "2026-09-30T12:00:00Z"));

        Assert.True(pred(At(U(2026, 9, 30, 12))));
        Assert.False(pred(At(U(2026, 9, 30, 12, 0, 1))));
    }

    [Fact]
    public async Task Between_dates_with_one_open_bound()
    {
        var until = await Pred(Between("updatedAt", null, "2026-09-30"));
        Assert.True(until(At(U(2001, 1, 1))));
        Assert.False(until(At(U(2026, 10, 1))));

        var since = await Pred(Between("updatedAt", "2026-09-01", null));
        Assert.True(since(At(U(2099, 1, 1))));
        Assert.False(since(At(U(2026, 8, 31, 23, 59, 59))));
    }

    [Fact]
    public async Task Between_dates_judges_from_after_to_against_the_whole_end_day()
    {
        // 10:00 on the 30th is inside "to 2026-09-30", which runs to the end of that day.
        var pred = await Pred(Between("updatedAt", "2026-09-30T10:00:00Z", "2026-09-30"));
        Assert.True(pred(At(U(2026, 9, 30, 11))));
    }

    [Fact]
    public async Task Between_dates_refuses_no_bounds_reversed_bounds_and_malformed_values()
    {
        Assert.Contains("at least one bound", await Error(Between("updatedAt", null, null)));
        Assert.Contains("'from' is after 'to'", await Error(Between("updatedAt", "2026-09-30", "2026-09-01")));
        Assert.Contains("[from, to]", await Error(new("updatedAt", FilterOperator.Between, null, new List<JsonElement> { J("2026-09-01") })));
        Assert.Contains("ISO-8601", await Error(Between("updatedAt", "last tuesday", null)));
    }

    // ---- Between on story points ----

    [Fact]
    public async Task Between_story_points_is_inclusive_at_both_ends_and_takes_decimals()
    {
        var pred = await Pred(Between("storyPoints", 1.5m, 3));

        Assert.True(pred(Pts(1.5m)));
        Assert.True(pred(Pts(3m)));
        Assert.False(pred(Pts(1.4m)));
        Assert.False(pred(Pts(3.01m)));
        Assert.False(pred(Pts(null)));
    }

    [Fact]
    public async Task Between_story_points_with_an_open_bound_still_leaves_out_unestimated_issues()
    {
        var pred = await Pred(Between("storyPoints", null, 3));

        Assert.True(pred(Pts(0m)));
        Assert.False(pred(Pts(null)));
    }

    [Fact]
    public async Task Between_story_points_refuses_reversed_bounds_and_non_numbers()
    {
        Assert.Contains("'from' is greater than 'to'", await Error(Between("storyPoints", 5, 3)));
        Assert.Contains("numbers", await Error(Between("storyPoints", "1", 3)));
    }

    [Theory]
    [InlineData("status")]
    [InlineData("summary")]
    public async Task Between_is_refused_on_fields_that_do_not_offer_it(string field) =>
        Assert.Equal($"Operator 'Between' is not allowed for field '{field}'.", await Error(Between(field, 1, 2)));
}
