// tests/JiraUnpacked.Tests/Tenancy/AmbientCurrentTenantTests.cs
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Infrastructure.Tenancy;

namespace JiraUnpacked.Tests.Tenancy;

public class AmbientCurrentTenantTests
{
    [Fact]
    public void EndUserId_is_null_outside_any_scope()
    {
        var t = new AmbientCurrentTenant();
        Assert.Null(t.EndUserId);
    }

    [Fact]
    public void BeginScope_sets_and_restores_on_dispose()
    {
        var t = new AmbientCurrentTenant();
        var id = Guid.NewGuid();
        using (t.BeginScope(id))
            Assert.Equal(id, t.EndUserId);
        Assert.Null(t.EndUserId);
    }

    [Fact]
    public void Nested_scopes_restore_the_outer_value()
    {
        var t = new AmbientCurrentTenant();
        var outer = Guid.NewGuid();
        var inner = Guid.NewGuid();
        using (t.BeginScope(outer))
        {
            using (t.BeginScope(inner))
                Assert.Equal(inner, t.EndUserId);
            Assert.Equal(outer, t.EndUserId);
        }
    }

    [Fact]
    public async Task Value_flows_across_async_awaits()
    {
        var t = new AmbientCurrentTenant();
        var id = Guid.NewGuid();
        using (t.BeginScope(id))
        {
            await Task.Yield();
            Assert.Equal(id, t.EndUserId);
        }
    }
}
