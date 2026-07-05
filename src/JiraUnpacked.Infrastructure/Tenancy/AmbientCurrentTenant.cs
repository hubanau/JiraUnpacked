using JiraUnpacked.Domain.Abstractions;

namespace JiraUnpacked.Infrastructure.Tenancy;

/// <summary>
/// AsyncLocal-backed tenant context shared by the HTTP pipeline and background jobs.
/// The value flows with the async context; BeginScope pushes/pops it.
/// </summary>
public sealed class AmbientCurrentTenant : ICurrentTenant, ITenantScope
{
    private static readonly AsyncLocal<Guid?> Current = new();

    public Guid? EndUserId => Current.Value;

    public IDisposable BeginScope(Guid endUserId)
    {
        var prior = Current.Value;
        Current.Value = endUserId;
        return new Restore(prior);
    }

    private sealed class Restore : IDisposable
    {
        private readonly Guid? _prior;
        private bool _disposed;
        public Restore(Guid? prior) => _prior = prior;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Current.Value = _prior;
        }
    }
}
