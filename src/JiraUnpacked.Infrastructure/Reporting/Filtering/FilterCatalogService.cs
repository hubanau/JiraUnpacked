using JiraUnpacked.Domain.Abstractions;

namespace JiraUnpacked.Infrastructure.Reporting.Filtering;

/// <summary>
/// Projects the internal FilterFields.All into public DTOs. FilterFields stays
/// internal on purpose: it holds compiled selector lambdas, an Infrastructure
/// detail that has no business in the API contract.
///
/// Both the field order and each field's operator order are made explicitly
/// deterministic here rather than left to the source collections' natural
/// enumeration: FilterFields.All is a Dictionary and each field's Ops is a
/// HashSet, and neither's enumeration order is guaranteed by the language.
/// Fields are sorted by name; operators are sorted by the explicit reading
/// order in <see cref="DisplayOrder"/> rather than FilterOperator's declared
/// (ordinal) order, since new members are appended to the enum without
/// regard to where they should read in the editor.
/// </summary>
public sealed class FilterCatalogService : IFilterCatalogService
{
    /// <summary>
    /// The order the editor offers operators in: equality, lists, comparisons, ranges and
    /// relative dates, text matching, then the two that take no value. Explicit because
    /// FilterOperator is append-only, which makes its declaration order a history rather than a
    /// reading order. Every member must appear here — FilterCatalogServiceTests checks.
    /// </summary>
    internal static readonly FilterOperator[] DisplayOrder =
    {
        FilterOperator.Eq, FilterOperator.Neq, FilterOperator.In, FilterOperator.NotIn,
        FilterOperator.Lt, FilterOperator.Lte, FilterOperator.Gt, FilterOperator.Gte,
        FilterOperator.Between, FilterOperator.InLast, FilterOperator.NotInLast, FilterOperator.InPeriod,
        FilterOperator.Contains, FilterOperator.NotContains, FilterOperator.StartsWith,
        FilterOperator.IsNull, FilterOperator.IsNotNull,
    };

    public FilterCatalogView Get() => new(
        FilterFields.All.Values
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .Select(f => new FilterFieldInfo(
                f.Name,
                f.Type.ToString(),
                f.Ops.OrderBy(o => Array.IndexOf(DisplayOrder, o)).Select(o => o.ToString()).ToList()))
            .ToList(),
        IssueFilterTranslator.MaxConditions,
        IssueFilterTranslator.MaxInValues,
        IssueFilterTranslator.MaxGroups,
        IssueFilterTranslator.MaxDepth);
}
