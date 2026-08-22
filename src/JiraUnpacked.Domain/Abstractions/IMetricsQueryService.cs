// src/JiraUnpacked.Domain/Abstractions/IMetricsQueryService.cs
namespace JiraUnpacked.Domain.Abstractions;

/// <summary>
/// Curated FR-E2 metrics. A null <c>filter</c> means the whole project — what the GET routes
/// ask for. A non-null one selects the population the metric is computed over: the filter
/// chooses which issues count, not which numbers survive.
///
/// Every method returns a null Report with a null Error when the project is unknown or belongs
/// to another tenant (=> 404, no existence leak), and a non-null Error for a rejected filter
/// (=> 400). The project check runs first, so a bad filter aimed at someone else's project
/// still answers 404.
/// </summary>
public interface IMetricsQueryService
{
    Task<ReportResult<StoryCountsReport>> GetStoryCountsAsync(
        Guid projectId, ReportFilter? filter = null, CancellationToken ct = default);

    Task<ReportResult<TimeInProgressReport>> GetTimeInProgressAsync(
        Guid projectId, ReportFilter? filter = null, CancellationToken ct = default);

    Task<ReportResult<ThroughputReport>> GetThroughputAsync(
        Guid projectId, ThroughputBucketSize bucket, DateTime? from, DateTime? to,
        ReportFilter? filter = null, CancellationToken ct = default);

    Task<ReportResult<StatusAgingReport>> GetStatusAgingAsync(
        Guid projectId, int page, int pageSize,
        ReportFilter? filter = null, CancellationToken ct = default);
}
