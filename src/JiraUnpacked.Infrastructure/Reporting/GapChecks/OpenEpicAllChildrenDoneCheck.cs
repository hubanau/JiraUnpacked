// src/JiraUnpacked.Infrastructure/Reporting/GapChecks/OpenEpicAllChildrenDoneCheck.cs
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Enums;

namespace JiraUnpacked.Infrastructure.Reporting.GapChecks;

/// <summary>The FR-E3 flagship: epics that are not Done even though every child is Done.</summary>
public sealed class OpenEpicAllChildrenDoneCheck : IGapCheck
{
    public GapCheckInfo Info { get; } = new(
        "open-epic-all-children-done",
        "Open epics whose children are all done",
        "Epics that are not Done even though every one of their child issues is Done.");

    public GapCheckOutcome Run(GapCheckContext context)
    {
        if (!context.HasEpicTypeConfigured) return GapCheckViolations.NotConfigured();

        var violations = new List<GapCheckViolation>();
        foreach (var epic in context.Issues)
        {
            if (!epic.IsEpicType || epic.Class == StatusClass.Done) continue;

            var children = context.ChildrenByParent[epic.JiraIssueId].ToList();

            // At least one child is required: "all children are done" is vacuously true for a
            // childless epic, which would flag every one of them.
            if (children.Count == 0) continue;
            if (!children.All(c => c.Class == StatusClass.Done)) continue;

            violations.Add(GapCheckViolations.From(epic, children));
        }

        return new GapCheckOutcome(GapCheckStatus.Ok, violations);
    }
}
