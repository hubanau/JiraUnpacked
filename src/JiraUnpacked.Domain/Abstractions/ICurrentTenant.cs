namespace JiraUnpacked.Domain.Abstractions;

/// <summary>The tenant (End User) for the current request, or null when unauthenticated.</summary>
public interface ICurrentTenant
{
    Guid? EndUserId { get; }
}
