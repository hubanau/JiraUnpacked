namespace JiraUnpacked.Domain.Abstractions;

/// <summary>
/// The filter half of a report request: the same `conditions` (flat AND) or `filter` (boolean
/// tree) pair the issue query accepts, and never both. Null, or both parts null/empty, means
/// the report covers the whole project — which is exactly what the GET routes ask for.
/// </summary>
public sealed record ReportFilter(List<FilterCondition>? Conditions, FilterGroup? Filter);

/// <summary>
/// Null Report + null Error => project unknown/cross-tenant (404). Non-null Error => bad
/// filter (400). Otherwise Report is the result. Mirrors IssueQueryResult so every report
/// endpoint maps outcomes to statuses the same way.
/// </summary>
public sealed record ReportResult<T>(T? Report, string? Error) where T : class
{
    public static ReportResult<T> NotFound() => new(null, null);
    public static ReportResult<T> Invalid(string error) => new(null, error);
    public static ReportResult<T> Ok(T report) => new(report, null);
}

/// <summary>Body of the filtered story-counts and time-in-progress POSTs.</summary>
public sealed record ReportFilterRequest(List<FilterCondition>? Conditions, FilterGroup? Filter)
{
    public ReportFilter ToFilter() => new(Conditions, Filter);
}

/// <summary>Body of the filtered throughput POST — the filter plus throughput's own window.</summary>
public sealed record ThroughputFilterRequest(
    List<FilterCondition>? Conditions, FilterGroup? Filter,
    string? Bucket, DateTime? From, DateTime? To)
{
    public ReportFilter ToFilter() => new(Conditions, Filter);
}

/// <summary>Body of the filtered status-aging and gap-check-run POSTs — the filter plus paging.</summary>
public sealed record PagedFilterRequest(
    List<FilterCondition>? Conditions, FilterGroup? Filter, int Page = 1, int PageSize = 50)
{
    public ReportFilter ToFilter() => new(Conditions, Filter);
}
