// src/JiraUnpacked.Domain/Abstractions/MetricViews.cs
namespace JiraUnpacked.Domain.Abstractions;

// Status is null when the issue has no status FK.
public sealed record StatusCount(string? Status, string Class, int Count, decimal StoryPoints);

public sealed record StoryCountsReport(
    IReadOnlyList<StatusCount> ByStatus, int TotalIssues, decimal TotalStoryPoints);

// AverageDays is null (not 0) when no issue qualifies, so the number is never a lie.
public sealed record TimeInProgressReport(
    double? AverageDays, int CompletedIssuesConsidered, int IssuesExcluded);

public sealed record ThroughputBucket(DateTime PeriodStart, int Completed);

public sealed record ThroughputReport(
    string Bucket, DateTime From, DateTime To, IReadOnlyList<ThroughputBucket> Buckets);

public sealed record AgingItem(
    string JiraIssueId, string JiraKey, string? Summary, string? CurrentStatus,
    string Class, double AgeDays, DateTime EnteredAt);

public sealed record AgingBand(string Band, int Count);

public sealed record StatusAgingReport(
    PagedResult<AgingItem> Items, IReadOnlyList<AgingBand> Histogram);

public enum ThroughputBucketSize { Day, Week, Month }

public static class ThroughputBucketSizes
{
    /// <summary>Parses the `bucket` query value. Null/empty defaults to Week; unknown values fail.</summary>
    public static bool TryParse(string? value, out ThroughputBucketSize size)
    {
        if (string.IsNullOrWhiteSpace(value)) { size = ThroughputBucketSize.Week; return true; }
        switch (value.Trim().ToLowerInvariant())
        {
            case "day": size = ThroughputBucketSize.Day; return true;
            case "week": size = ThroughputBucketSize.Week; return true;
            case "month": size = ThroughputBucketSize.Month; return true;
            default: size = ThroughputBucketSize.Week; return false;
        }
    }
}
