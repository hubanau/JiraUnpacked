namespace JiraUnpacked.Domain.Abstractions;

public sealed record FilterFieldInfo(string Name, string Type, IReadOnlyList<string> Operators);

public sealed record FilterCatalogView(
    IReadOnlyList<FilterFieldInfo> Fields, int MaxConditions, int MaxInValues);

/// <summary>
/// The closed filter-field allow-list, exposed so clients need not mirror it.
/// Static and tenant-independent — identical for every caller.
/// </summary>
public interface IFilterCatalogService
{
    FilterCatalogView Get();
}
