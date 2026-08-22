namespace JiraUnpacked.Domain.Abstractions;

public sealed record FilterFieldInfo(string Name, string Type, IReadOnlyList<string> Operators);

/// <summary>
/// The field matrix plus every guard the server enforces. The guards are served for the same
/// reason the fields are: a client that has to guess a limit will hand-maintain a copy of it,
/// and that copy will drift.
/// </summary>
public sealed record FilterCatalogView(
    IReadOnlyList<FilterFieldInfo> Fields,
    int MaxConditions, int MaxInValues, int MaxGroups, int MaxDepth);

/// <summary>
/// The closed filter-field allow-list, exposed so clients need not mirror it.
/// Static and tenant-independent — identical for every caller.
/// </summary>
public interface IFilterCatalogService
{
    FilterCatalogView Get();
}
