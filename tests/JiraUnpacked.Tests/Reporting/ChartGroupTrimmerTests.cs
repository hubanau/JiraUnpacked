using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Infrastructure.Reporting;

namespace JiraUnpacked.Tests.Reporting;

public sealed class ChartGroupTrimmerTests
{
    private static ChartGroup G(string key, int issues, decimal points) => new(key, issues, points);

    [Fact]
    public void Keeps_everything_under_the_cap()
    {
        var all = new[] { G("a", 3, 1), G("b", 2, 2), G("c", 1, 3) };

        var (kept, other) = ChartGroupTrimmer.Trim(all, keyOrdered: false);

        Assert.Equal(all, kept);
        Assert.Null(other);
    }

    [Fact]
    public void Keeps_top_by_either_measure()
    {
        // C has one issue but the most points: a points chart must show it, so it survives even
        // though a count-only cut would drop it.
        var all = new[] { G("A", 10, 0), G("B", 9, 0), G("C", 1, 100), G("D", 1, 0) };

        var (kept, other) = ChartGroupTrimmer.Trim(all, keyOrdered: false, cap: 2);

        Assert.Equal(new[] { "A", "B", "C" }, kept.Select(g => g.Key));
        Assert.Equal(new ChartOther(1, 1, 0), other);
    }

    [Fact]
    public void Key_ordered_keeps_the_largest_keys()
    {
        // A timeline keeps its newest months, whatever their size.
        var all = new[] { G("2026-01", 50, 50), G("2026-02", 1, 1), G("2026-03", 2, 2) };

        var (kept, other) = ChartGroupTrimmer.Trim(all, keyOrdered: true, cap: 2);

        Assert.Equal(new[] { "2026-02", "2026-03" }, kept.Select(g => g.Key));
        Assert.Equal(new ChartOther(1, 50, 50), other);
    }

    [Fact]
    public void Kept_order_is_input_order()
    {
        var all = new[] { G("x", 1, 9), G("y", 9, 1), G("z", 5, 5), G("w", 0, 0) };

        var (kept, _) = ChartGroupTrimmer.Trim(all, keyOrdered: false, cap: 1);

        Assert.Equal(new[] { "x", "y" }, kept.Select(g => g.Key));
    }
}
