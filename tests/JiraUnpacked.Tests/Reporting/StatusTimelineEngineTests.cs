using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Reporting;

namespace JiraUnpacked.Tests.Reporting;

public sealed class StatusTimelineEngineTests
{
    private static readonly DateTime Created = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // "To Do" => Other, "In Progress" => Active, "Done" => Done.
    private static Dictionary<string, StatusClass> Semantics() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["To Do"] = StatusClass.Other,
            ["In Progress"] = StatusClass.Active,
            ["Done"] = StatusClass.Done,
        };

    private static StatusTransition T(int day, string? from, string? to) =>
        new(new DateTime(2024, 1, day, 0, 0, 0, DateTimeKind.Utc), from, to);

    [Fact]
    public void Reconstructs_initial_status_from_first_from_value()
    {
        // The pre-first-transition segment is "To Do", effective from creation.
        var t = StatusTimelineEngine.Build(Created, "In Progress",
            new[] { T(3, "To Do", "In Progress") }, Semantics());

        Assert.Equal(new DateTime(2024, 1, 3, 0, 0, 0, DateTimeKind.Utc), t.FirstActiveAt);
        Assert.Null(t.FirstDoneAt);
        Assert.Equal("In Progress", t.CurrentStatus);
        Assert.Equal(StatusClass.Active, t.CurrentClass);
        Assert.Equal(new DateTime(2024, 1, 3, 0, 0, 0, DateTimeKind.Utc), t.CurrentEnteredAt);
    }

    [Fact]
    public void No_transitions_yields_single_segment_from_creation()
    {
        var t = StatusTimelineEngine.Build(Created, "To Do",
            Array.Empty<StatusTransition>(), Semantics());

        Assert.Null(t.FirstActiveAt);
        Assert.Null(t.FirstDoneAt);
        Assert.Equal("To Do", t.CurrentStatus);
        Assert.Equal(StatusClass.Other, t.CurrentClass);
        Assert.Equal(Created, t.CurrentEnteredAt);   // entered at creation
    }

    [Fact]
    public void Null_from_value_skips_the_pre_transition_segment()
    {
        // Defensive: unknown prior status => the timeline starts at the transition itself,
        // NOT at creation.
        var t = StatusTimelineEngine.Build(Created, "In Progress",
            new[] { T(4, null, "In Progress") }, Semantics());

        Assert.Equal(new DateTime(2024, 1, 4, 0, 0, 0, DateTimeKind.Utc), t.FirstActiveAt);
        Assert.Equal(new DateTime(2024, 1, 4, 0, 0, 0, DateTimeKind.Utc), t.CurrentEnteredAt);
    }

    [Fact]
    public void Done_without_ever_being_active_still_records_first_done()
    {
        // The independence property: throughput must count this issue even though
        // it never passed through an Active status.
        var t = StatusTimelineEngine.Build(Created, "Done",
            new[] { T(5, "To Do", "Done") }, Semantics());

        Assert.Null(t.FirstActiveAt);
        Assert.Equal(new DateTime(2024, 1, 5, 0, 0, 0, DateTimeKind.Utc), t.FirstDoneAt);
        Assert.Equal(StatusClass.Done, t.CurrentClass);
    }

    [Fact]
    public void Reopened_issue_keeps_the_first_done_and_the_later_current()
    {
        var t = StatusTimelineEngine.Build(Created, "In Progress", new[]
        {
            T(2, "To Do", "In Progress"),
            T(5, "In Progress", "Done"),
            T(9, "Done", "In Progress"),   // reopened
        }, Semantics());

        Assert.Equal(new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc), t.FirstActiveAt);
        Assert.Equal(new DateTime(2024, 1, 5, 0, 0, 0, DateTimeKind.Utc), t.FirstDoneAt); // FIRST done
        Assert.Equal(StatusClass.Active, t.CurrentClass);                                  // not Done now
        Assert.Equal(new DateTime(2024, 1, 9, 0, 0, 0, DateTimeKind.Utc), t.CurrentEnteredAt);
    }

    [Fact]
    public void Unmapped_status_name_classifies_as_other()
    {
        var t = StatusTimelineEngine.Build(Created, "Blocked",
            new[] { T(3, "To Do", "Blocked") }, Semantics());

        Assert.Null(t.FirstActiveAt);
        Assert.Null(t.FirstDoneAt);
        Assert.Equal(StatusClass.Other, t.CurrentClass);
    }

    [Fact]
    public void Classification_is_case_insensitive()
    {
        var t = StatusTimelineEngine.Build(Created, "IN PROGRESS",
            new[] { T(3, "to do", "IN PROGRESS") }, Semantics());

        Assert.Equal(StatusClass.Active, t.CurrentClass);
        Assert.Equal(new DateTime(2024, 1, 3, 0, 0, 0, DateTimeKind.Utc), t.FirstActiveAt);
    }
}
