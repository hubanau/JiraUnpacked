using JiraUnpacked.Domain.Abstractions;

namespace JiraUnpacked.Infrastructure.Time;

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
