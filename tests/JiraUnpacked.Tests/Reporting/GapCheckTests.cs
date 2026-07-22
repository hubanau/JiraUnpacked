using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Reporting.GapChecks;

namespace JiraUnpacked.Tests.Reporting;

public sealed class GapCheckTests
{
    private static GapCheckIssue Epic(string id, string key, StatusClass cls) =>
        new(id, key, "epic", cls.ToString(), cls, "Epic", true, null);

    private static GapCheckIssue Child(string id, string key, StatusClass cls, string parent) =>
        new(id, key, "child", cls.ToString(), cls, "Story", false, parent);

    private static GapCheckContext Ctx(params GapCheckIssue[] issues) => new(issues, hasEpicTypeConfigured: true);

    // ---- flagship: open epic, all children done ----

    [Fact]
    public void Flagship_flags_open_epic_whose_children_are_all_done()
    {
        var ctx = Ctx(
            Epic("1", "ACME-1", StatusClass.Active),
            Child("2", "ACME-2", StatusClass.Done, "1"),
            Child("3", "ACME-3", StatusClass.Done, "1"));

        var outcome = new OpenEpicAllChildrenDoneCheck().Run(ctx);

        Assert.Equal(GapCheckStatus.Ok, outcome.Status);
        var v = Assert.Single(outcome.Violations);
        Assert.Equal("ACME-1", v.JiraKey);
        Assert.Equal(2, v.Children.Count);          // evidence: all children
    }

    [Fact]
    public void Flagship_does_not_flag_a_childless_epic()
    {
        // "All children are done" is vacuously true with zero children; without the
        // at-least-one guard every childless epic would be reported.
        var outcome = new OpenEpicAllChildrenDoneCheck().Run(Ctx(Epic("1", "ACME-1", StatusClass.Active)));

        Assert.Equal(GapCheckStatus.Ok, outcome.Status);
        Assert.Empty(outcome.Violations);
    }

    [Fact]
    public void Flagship_does_not_flag_when_a_child_is_still_open()
    {
        var ctx = Ctx(
            Epic("1", "ACME-1", StatusClass.Active),
            Child("2", "ACME-2", StatusClass.Done, "1"),
            Child("3", "ACME-3", StatusClass.Active, "1"));

        Assert.Empty(new OpenEpicAllChildrenDoneCheck().Run(ctx).Violations);
    }

    [Fact]
    public void Flagship_does_not_flag_an_epic_that_is_already_done()
    {
        var ctx = Ctx(
            Epic("1", "ACME-1", StatusClass.Done),
            Child("2", "ACME-2", StatusClass.Done, "1"));

        Assert.Empty(new OpenEpicAllChildrenDoneCheck().Run(ctx).Violations);
    }

    // ---- inverse: done epic, open children ----

    [Fact]
    public void Inverse_flags_done_epic_and_lists_only_the_open_children()
    {
        var ctx = Ctx(
            Epic("1", "ACME-1", StatusClass.Done),
            Child("2", "ACME-2", StatusClass.Done, "1"),
            Child("3", "ACME-3", StatusClass.Active, "1"));

        var outcome = new DoneEpicOpenChildrenCheck().Run(ctx);

        var v = Assert.Single(outcome.Violations);
        Assert.Equal("ACME-1", v.JiraKey);
        var child = Assert.Single(v.Children);       // evidence: ONLY the open child
        Assert.Equal("ACME-3", child.JiraKey);
    }

    [Fact]
    public void Inverse_does_not_flag_when_every_child_is_done()
    {
        var ctx = Ctx(
            Epic("1", "ACME-1", StatusClass.Done),
            Child("2", "ACME-2", StatusClass.Done, "1"));

        Assert.Empty(new DoneEpicOpenChildrenCheck().Run(ctx).Violations);
    }

    // ---- shared behaviour ----

    [Fact]
    public void Both_checks_report_not_configured_when_no_epic_type_is_set()
    {
        var ctx = new GapCheckContext(
            new[] { Epic("1", "ACME-1", StatusClass.Active), Child("2", "ACME-2", StatusClass.Done, "1") },
            hasEpicTypeConfigured: false);

        foreach (var check in new IGapCheck[] { new OpenEpicAllChildrenDoneCheck(), new DoneEpicOpenChildrenCheck() })
        {
            var outcome = check.Run(ctx);
            Assert.Equal(GapCheckStatus.NotConfigured, outcome.Status);
            Assert.Empty(outcome.Violations);
        }
    }

    [Fact]
    public void Non_epic_parents_are_ignored_even_when_their_children_are_all_done()
    {
        var parent = new GapCheckIssue("1", "ACME-1", "story", "Active", StatusClass.Active, "Story", false, null);
        var ctx = Ctx(parent, Child("2", "ACME-2", StatusClass.Done, "1"));

        Assert.Empty(new OpenEpicAllChildrenDoneCheck().Run(ctx).Violations);
    }

    [Fact]
    public void Check_ids_are_stable()
    {
        Assert.Equal("open-epic-all-children-done", new OpenEpicAllChildrenDoneCheck().Info.Id);
        Assert.Equal("done-epic-open-children", new DoneEpicOpenChildrenCheck().Info.Id);
    }
}
