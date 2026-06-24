namespace JiraUnpacked.Domain.Abstractions;

/// <summary>UTC time source, injected so time-dependent logic is testable.</summary>
public interface IClock
{
    DateTime UtcNow { get; }
}
