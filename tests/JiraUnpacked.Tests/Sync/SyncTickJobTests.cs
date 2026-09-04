// tests/JiraUnpacked.Tests/Sync/SyncTickJobTests.cs
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Infrastructure.Sync;
using JiraUnpacked.Infrastructure.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace JiraUnpacked.Tests.Sync;

public class SyncTickJobTests
{
    private sealed class OneDueFinder : IDueProjectFinder
    {
        private readonly DueProject _p;
        public OneDueFinder(DueProject p) => _p = p;
        public Task<IReadOnlyList<DueProject>> GetDueProjectsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DueProject>>(new[] { _p });
    }

    private sealed class RecordingOrchestrator : ISyncOrchestrator
    {
        public readonly List<(Guid tenant, Guid project)> Calls = new();
        private readonly AmbientCurrentTenant _tenant;
        public RecordingOrchestrator(AmbientCurrentTenant tenant) => _tenant = tenant;
        public Task<SyncRunResult> RunProjectAsync(Guid projectId, CancellationToken ct = default)
        {
            Calls.Add((_tenant.EndUserId ?? Guid.Empty, projectId)); // captures the ambient tenant at call time
            return Task.FromResult(new SyncRunResult(SyncOutcome.Completed, null));
        }

        public Task<SyncRunResult> RunRederiveAsync(Guid projectId, CancellationToken ct = default)
            => Task.FromResult(new SyncRunResult(SyncOutcome.Completed, null));

        // The tick calls the both-halves entry point above; the split halves exist for the
        // request/worker hand-off and are not this job's path.
        public Task<SyncRunResult> StartProjectAsync(Guid projectId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<SyncRunResult> StartRederiveAsync(Guid projectId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<SyncRunResult> ExecuteProjectAsync(Guid runId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<SyncRunResult> ExecuteRederiveAsync(Guid runId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    [Fact]
    public async Task Runs_each_due_project_inside_its_tenant_scope()
    {
        var ambient = new AmbientCurrentTenant();
        var tenant = Guid.NewGuid();
        var project = Guid.NewGuid();
        var orch = new RecordingOrchestrator(ambient);

        var services = new ServiceCollection();
        services.AddSingleton<ISyncOrchestrator>(orch);
        var provider = services.BuildServiceProvider();

        var job = new SyncTickJob(
            new OneDueFinder(new DueProject(project, tenant, 60)),
            ambient, provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SyncTickJob>.Instance);

        await job.RunOnceAsync(CancellationToken.None);

        Assert.Contains((tenant, project), orch.Calls); // ran with the ambient tenant set to the owner
    }
}
