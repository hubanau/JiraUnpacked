using JiraUnpacked.Domain.Abstractions;

namespace JiraUnpacked.Tests.Security;

public sealed class FakeClock : IClock
{
    public FakeClock(DateTime utcNow) => UtcNow = utcNow;
    public DateTime UtcNow { get; set; }
}
