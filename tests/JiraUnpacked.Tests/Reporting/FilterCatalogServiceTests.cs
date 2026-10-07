using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Infrastructure.Reporting.Filtering;

namespace JiraUnpacked.Tests.Reporting;

public sealed class FilterCatalogServiceTests
{
    private static List<string> OpsOf(string field) =>
        new FilterCatalogService().Get().Fields.Single(f => f.Name == field).Operators.ToList();

    [Fact]
    public void Operators_are_listed_in_reading_order_not_declaration_order()
    {
        // FilterOperator is append-only (NotIn/IsNotNull/NotContains went last), so ordinal order
        // would put "is empty" between "is any of" and "is none of" in the editor.
        Assert.Equal(new[] { "Eq", "Neq", "In", "NotIn", "IsNull", "IsNotNull" }, OpsOf("status"));
        Assert.Equal(new[] { "Eq", "Neq", "In", "NotIn", "IsNull", "IsNotNull" }, OpsOf("statusCategory"));
        Assert.Equal(new[] { "Eq", "Neq", "Lt", "Lte", "Gt", "Gte", "Between", "IsNull", "IsNotNull" }, OpsOf("storyPoints"));
        Assert.Equal(new[] { "Lt", "Lte", "Gt", "Gte", "Between", "InLast", "NotInLast", "InPeriod" }, OpsOf("createdAt"));
        Assert.Equal(new[] { "Lt", "Lte", "Gt", "Gte", "Between", "InLast", "NotInLast", "InPeriod" }, OpsOf("updatedAt"));
        Assert.Equal(new[] { "Contains", "NotContains", "StartsWith", "IsNull", "IsNotNull" }, OpsOf("summary"));
        Assert.Equal(new[] { "Eq", "In", "NotIn", "Contains", "StartsWith" }, OpsOf("jiraKey"));
        Assert.Equal(new[] { "Eq", "IsNull", "IsNotNull" }, OpsOf("parent"));
        Assert.Equal(new[] { "Changed" }, OpsOf("history"));
    }

    [Fact]
    public void The_history_cap_is_served()
    {
        Assert.Equal(5, new FilterCatalogService().Get().MaxHistoryConditions);
        Assert.Equal("History", new FilterCatalogService().Get().Fields.Single(f => f.Name == "history").Type);
    }

    [Fact]
    public void Every_operator_has_a_display_position()
    {
        foreach (var op in Enum.GetValues<FilterOperator>())
            Assert.Contains(op, FilterCatalogService.DisplayOrder);
    }
}
