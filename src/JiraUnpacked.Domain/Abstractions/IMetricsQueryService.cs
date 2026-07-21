// src/JiraUnpacked.Domain/Abstractions/IMetricsQueryService.cs
namespace JiraUnpacked.Domain.Abstractions;

/// <summary>
/// Curated FR-E2 metrics. Every method returns null when the project is unknown or
/// belongs to another tenant (=> the controller returns 404, no existence leak).
/// </summary>
public interface IMetricsQueryService
{
    Task<StoryCountsReport?> GetStoryCountsAsync(Guid projectId, CancellationToken ct = default);

    Task<TimeInProgressReport?> GetTimeInProgressAsync(Guid projectId, CancellationToken ct = default);

    Task<ThroughputReport?> GetThroughputAsync(
        Guid projectId, ThroughputBucketSize bucket, DateTime? from, DateTime? to, CancellationToken ct = default);

    Task<StatusAgingReport?> GetStatusAgingAsync(
        Guid projectId, int page, int pageSize, CancellationToken ct = default);
}
