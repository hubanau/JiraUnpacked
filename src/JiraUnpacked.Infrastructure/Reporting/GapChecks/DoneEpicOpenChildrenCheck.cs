// src/JiraUnpacked.Infrastructure/Reporting/GapChecks/DoneEpicOpenChildrenCheck.cs
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Enums;

namespace JiraUnpacked.Infrastructure.Reporting.GapChecks;

/// <summary>The inverse gap: epics marked Done that still have unfinished children.</summary>
public sealed class DoneEpicOpenChildrenCheck : IGapCheck
{
    public GapCheckInfo Info { get; } = new(
        "done-epic-open-children",
        "Done epics with open children",
        "Epics marked Done that still have child issues which are not Done.");

    public GapCheckOutcome Run(GapCheckContext context)
    {
        if (!context.HasEpicTypeConfigured) return GapCheckViolations.NotConfigured();

        var violations = new List<GapCheckViolation>();
        foreach (var epic in context.Issues)
        {
            if (!epic.IsEpicType || epic.Class != StatusClass.Done) continue;

            // Evidence is only the children that are NOT done — those are what needs attention.
            var open = context.ChildrenByParent[epic.JiraIssueId]
                .Where(c => c.Class != StatusClass.Done)
                .ToList();

            if (open.Count == 0) continue;

            violations.Add(GapCheckViolations.From(epic, open));
        }

        return new GapCheckOutcome(GapCheckStatus.Ok, violations);
    }
}
