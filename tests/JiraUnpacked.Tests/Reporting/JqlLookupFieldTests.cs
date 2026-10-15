using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Infrastructure.Reporting.Jql;
using static JiraUnpacked.Tests.Reporting.JqlTestKit;

namespace JiraUnpacked.Tests.Reporting;

/// <summary>The fields JQL names by id or by custom field, through a lookup like the one IssueJqlService loads.</summary>
public sealed class JqlLookupFieldTests
{
    private static readonly JqlLookup Lookup = new(
        Ids(("Alice", new[] { "acc-alice" }), ("Sam", new[] { "acc-sam-1", "acc-sam-2" })),
        Ids(("Sprint 7", new[] { "107" }), ("Sprint 8", new[] { "108" })),
        Ids(("Core", new[] { "team-1" })),
        "customfield_10001",
        "customfield_10016");

    private static object Smart(string key) => new { smart = key };

    [Fact]
    public void Assignee_is_named_by_account_id()
    {
        Assert.Equal("assignee = \"acc-alice\"", Where(C("assignee", FilterOperator.Eq, "Alice"), Lookup));
        // Two accounts share the name: the filter matches both, so the JQL lists both.
        Assert.Equal("assignee in (\"acc-sam-1\", \"acc-sam-2\")", Where(C("assignee", FilterOperator.Eq, "Sam"), Lookup));
        Assert.Equal("(assignee in (\"acc-alice\") OR assignee is EMPTY)", Where(L("assignee", FilterOperator.In, "Alice", null), Lookup));
        Assert.Equal("assignee != \"acc-alice\"", Where(C("assignee", FilterOperator.Neq, "Alice"), Lookup));
        Assert.Equal("assignee not in (\"acc-alice\", \"acc-sam-1\", \"acc-sam-2\")",
            Where(L("assignee", FilterOperator.NotIn, "Alice", "Sam"), Lookup));
        Assert.Equal("assignee is EMPTY", Where(C("assignee", FilterOperator.IsNull), Lookup));
    }

    [Fact]
    public void A_name_with_no_account_is_left_out_of_the_list_with_a_warning()
    {
        var t = Translate(All(L("assignee", FilterOperator.In, "Alice", "Ghost")), Lookup);
        Assert.Equal("assignee in (\"acc-alice\")", Where(t));
        Assert.Equal(new[]
        {
            "No Jira account in this project's data is named 'Ghost'; this list matches no issue by that name either, so the JQL leaves it out.",
        }, t.Warnings);

        // "is not Ghost" excludes nobody, here and in Jira: it means "has an assignee".
        Assert.Equal("assignee is not EMPTY", Where(C("assignee", FilterOperator.Neq, "Ghost"), Lookup));
    }

    [Fact]
    public void A_condition_on_a_name_with_no_account_is_left_out()
    {
        var t = Translate(All(C("status", FilterOperator.Eq, "Done"), C("assignee", FilterOperator.Eq, "Ghost")), Lookup);
        Assert.Equal("status = \"Done\"", Where(t));
        Assert.Contains(t.Warnings, w =>
            w.StartsWith("Left out \"assignee Eq Ghost\" (no Jira account in this project's data has that name)", StringComparison.Ordinal)
            && w.Contains("wider", StringComparison.Ordinal));
    }

    [Fact]
    public void Sprint_is_named_by_id_and_smart_values_by_sprint_functions()
    {
        Assert.Equal("sprint = 107", Where(C("sprint", FilterOperator.Eq, "Sprint 7"), Lookup));
        Assert.Equal("sprint in openSprints()", Where(C("sprint", FilterOperator.Eq, Smart("open")), Lookup));
        Assert.Equal("sprint in futureSprints()", Where(C("sprint", FilterOperator.Eq, Smart("future")), Lookup));
        Assert.Equal("sprint in closedSprints()", Where(C("sprint", FilterOperator.Eq, Smart("closed")), Lookup));
        Assert.Equal("(sprint in openSprints() AND sprint not in futureSprints())",
            Where(C("sprint", FilterOperator.Eq, Smart("active")), Lookup));
        Assert.Equal("(sprint in (107) OR (sprint in openSprints() AND sprint not in futureSprints()) OR sprint is EMPTY)",
            Where(L("sprint", FilterOperator.In, "Sprint 7", Smart("active"), null), Lookup));
        Assert.Equal("(sprint not in openSprints() OR sprint in futureSprints())",
            Where(C("sprint", FilterOperator.Neq, Smart("active")), Lookup));
        Assert.Equal("(sprint not in (108) AND sprint not in closedSprints())",
            Where(L("sprint", FilterOperator.NotIn, "Sprint 8", Smart("closed")), Lookup));
        Assert.Equal("sprint is EMPTY", Where(C("sprint", FilterOperator.IsNull), Lookup));
    }

    [Fact]
    public void Sprint_conditions_warn_once_that_jira_matches_every_sprint_an_issue_was_in()
    {
        var t = Translate(All(C("sprint", FilterOperator.Eq, "Sprint 7"), C("sprint", FilterOperator.Neq, "Sprint 8")), Lookup);
        Assert.Contains("every sprint it has been in", Assert.Single(t.Warnings));
        Assert.Empty(Translate(All(C("sprint", FilterOperator.IsNull)), Lookup).Warnings);
    }

    [Fact]
    public void Team_uses_the_mapped_custom_field_and_team_ids()
    {
        Assert.Equal("cf[10001] = \"team-1\"", Where(C("team", FilterOperator.Eq, "Core"), Lookup));
        Assert.Equal("cf[10001] is EMPTY", Where(C("team", FilterOperator.IsNull), Lookup));

        var unmapped = Translate(All(C("status", FilterOperator.Eq, "Done"), C("team", FilterOperator.Eq, "Core")),
            Lookup with { TeamField = null });
        Assert.Equal("status = \"Done\"", Where(unmapped));
        Assert.Contains("(no Team field is mapped for this project)", Assert.Single(unmapped.Warnings));

        var notCustom = Translate(All(C("status", FilterOperator.Eq, "Done"), C("team", FilterOperator.Eq, "Core")),
            Lookup with { TeamField = "team" });
        Assert.Contains("the Team mapping 'team' is not a custom field JQL can name", Assert.Single(notCustom.Warnings));
    }

    [Fact]
    public void Story_points_use_the_mapped_custom_field()
    {
        Assert.Equal("cf[10016] = 3", Where(C("storyPoints", FilterOperator.Eq, 3), Lookup));
        Assert.Equal("cf[10016] != 2.5", Where(C("storyPoints", FilterOperator.Neq, 2.5m), Lookup));
        Assert.Equal("cf[10016] < 3", Where(C("storyPoints", FilterOperator.Lt, 3), Lookup));
        Assert.Equal("cf[10016] <= 3", Where(C("storyPoints", FilterOperator.Lte, 3), Lookup));
        Assert.Equal("cf[10016] > 3", Where(C("storyPoints", FilterOperator.Gt, 3), Lookup));
        Assert.Equal("cf[10016] >= 3", Where(C("storyPoints", FilterOperator.Gte, 3), Lookup));
        Assert.Equal("(cf[10016] >= 1 AND cf[10016] <= 3)", Where(L("storyPoints", FilterOperator.Between, 1, 3), Lookup));
        Assert.Equal("cf[10016] <= 3", Where(L("storyPoints", FilterOperator.Between, null, 3), Lookup));
        Assert.Equal("cf[10016] is not EMPTY", Where(C("storyPoints", FilterOperator.IsNotNull), Lookup));

        var unmapped = Translate(All(C("storyPoints", FilterOperator.Eq, 3)), Lookup with { StoryPointsField = null });
        Assert.Equal("project = \"ACME\" ORDER BY updated DESC, key ASC", unmapped.Jql);
        Assert.Contains("(no Story Points field is mapped for this project)", Assert.Single(unmapped.Warnings));
    }

    [Fact]
    public void Team_and_story_points_sort_by_their_mapped_fields()
    {
        Assert.EndsWith(" ORDER BY cf[10001] DESC, key ASC", Translate(null, Lookup, "team").Jql);
        Assert.EndsWith(" ORDER BY cf[10016] ASC, key ASC", Translate(null, Lookup, "storyPoints", false).Jql);
        var t = Translate(null, Lookup with { StoryPointsField = null }, "storyPoints");
        Assert.EndsWith(" ORDER BY key ASC", t.Jql);
        Assert.Equal(new[] { "The JQL cannot sort by 'storyPoints', so it sorts by key." }, t.Warnings);
    }
}
