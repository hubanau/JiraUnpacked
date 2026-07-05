namespace JiraUnpacked.Domain.Abstractions;

/// <summary>Establishes the ambient tenant for a unit of work (HTTP request or background job).</summary>
public interface ITenantScope
{
    IDisposable BeginScope(Guid endUserId);
}
