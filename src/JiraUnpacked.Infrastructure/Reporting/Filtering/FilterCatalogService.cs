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
/// Fields are sorted by name; operators are sorted by FilterOperator's
/// declared enum order (its ordinal), which the language DOES guarantee is
/// stable. This also happens to reproduce the operator order the client's
/// former hand-maintained mirror used, since that mirror was written by
/// reading FilterField.cs's Ops(...) calls, which list operators in the same
/// enum-declaration order.
/// </summary>
public sealed class FilterCatalogService : IFilterCatalogService
{
    public FilterCatalogView Get() => new(
        FilterFields.All.Values
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .Select(f => new FilterFieldInfo(
                f.Name,
                f.Type.ToString(),
                f.Ops.OrderBy(o => (int)o).Select(o => o.ToString()).ToList()))
            .ToList(),
        IssueFilterTranslator.MaxConditions,
        IssueFilterTranslator.MaxInValues);
}
