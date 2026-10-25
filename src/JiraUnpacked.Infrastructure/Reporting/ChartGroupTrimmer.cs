using JiraUnpacked.Domain.Abstractions;

namespace JiraUnpacked.Infrastructure.Reporting;

/// <summary>
/// Bounds a chart's groups without making either measure lie. The grouped rows are one per
/// distinct value, so they are trimmed in memory rather than in SQL.
/// </summary>
public static class ChartGroupTrimmer
{
    /// <summary>
    /// A group survives when it is in the top <paramref name="cap"/> by issues OR by story points,
    /// so whichever measure the client shows, its largest groups are all there. A key-ordered
    /// field (months, Points) is a scale, not a ranking: it keeps its last <paramref name="cap"/>
    /// keys — the newest months, the largest estimates. Survivors keep the input's order; the rest
    /// are summed into Other so the totals still add up.
    /// </summary>
    public static (IReadOnlyList<ChartGroup> Kept, ChartOther? Other) Trim(
        IReadOnlyList<ChartGroup> all, bool keyOrdered, int cap = 500)
    {
        if (all.Count <= cap) return (all, null);

        HashSet<int> keep;
        if (keyOrdered)
        {
            keep = Enumerable.Range(all.Count - cap, cap).ToHashSet();
        }
        else
        {
            // OrderByDescending is stable: on a tie the earlier group (the server's own order) wins.
            var indexes = Enumerable.Range(0, all.Count).ToList();
            keep = indexes.OrderByDescending(i => all[i].Issues).Take(cap)
                .Concat(indexes.OrderByDescending(i => all[i].StoryPoints).Take(cap))
                .ToHashSet();
        }

        var kept = new List<ChartGroup>(keep.Count);
        int groups = 0, issues = 0;
        decimal points = 0;
        for (var i = 0; i < all.Count; i++)
        {
            if (keep.Contains(i)) { kept.Add(all[i]); continue; }
            groups++;
            issues += all[i].Issues;
            points += all[i].StoryPoints;
        }

        return (kept, new ChartOther(groups, issues, points));
    }
}
