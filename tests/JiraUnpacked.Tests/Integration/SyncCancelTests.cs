// tests/JiraUnpacked.Tests/Integration/SyncCancelTests.cs
using System.Net;
using JiraUnpacked.Domain.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace JiraUnpacked.Tests.Integration;

/// <summary>
/// Stopping a run that is already going.
///
/// The request and the worker are in different scopes and different call stacks — the request
/// that started the run answered 202 long ago — so the only thing joining them is the
/// cancellation registry, and these assert through the real HTTP endpoint rather than against
/// that registry directly.
/// </summary>
public sealed class SyncCancelTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public SyncCancelTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Stopping_a_project_with_nothing_running_is_not_found()
    {
        // A stop that silently succeeds having stopped nothing is worse than one that says so:
        // the caller would believe a run had been ended when it is still going, or never was.
        var client = await TestData.AuthedClientAsync(_factory);

        var resp = await client.PostAsync($"/api/projects/{Guid.NewGuid()}/sync/cancel", null);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task A_run_this_process_is_not_executing_cannot_be_cancelled()
    {
        // A Running row left behind by a previous process is orphaned, not cancellable: its
        // CancellationTokenSource died with the process that held it. Reporting success here
        // would promise something no code can deliver — that run belongs to the reaper.
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);
        await SyncCancelTestData.SeedRunningRunAsync(tenant, ctx.ProjectId);

        var resp = await client.PostAsync($"/api/projects/{ctx.ProjectId}/sync/cancel", null);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public void Cancelling_is_remembered_after_the_registration_is_gone()
    {
        // The orchestrator asks WHY it was cancelled while unwinding — after the worker's
        // registration has been disposed. If the answer were forgotten with the registration, a
        // deliberate stop would be recorded as an unexpected failure.
        var cancellation = _factory.Services.GetRequiredService<ISyncCancellation>();
        var runId = Guid.NewGuid();

        using (cancellation.Register(runId, CancellationToken.None, out var token))
        {
            Assert.False(token.IsCancellationRequested);
            Assert.True(cancellation.Cancel(runId));
            Assert.True(token.IsCancellationRequested);
        }

        Assert.True(cancellation.WasCancelled(runId));

        // And the run is no longer cancellable, because it is no longer running.
        Assert.False(cancellation.Cancel(runId));
    }

    [Fact]
    public void An_unregistered_run_reports_that_it_could_not_be_cancelled()
    {
        var cancellation = _factory.Services.GetRequiredService<ISyncCancellation>();

        Assert.False(cancellation.Cancel(Guid.NewGuid()));
    }

    [Fact]
    public void Shutting_down_cancels_the_run_without_marking_it_cancelled()
    {
        // The token is linked to the host's, so a shutdown stops the work — but nobody PRESSED
        // stop, and the difference decides whether the run is recorded Cancelled or left for the
        // next process to pick up.
        var cancellation = _factory.Services.GetRequiredService<ISyncCancellation>();
        var runId = Guid.NewGuid();
        using var host = new CancellationTokenSource();

        using (cancellation.Register(runId, host.Token, out var token))
        {
            host.Cancel();
            Assert.True(token.IsCancellationRequested);
            Assert.False(cancellation.WasCancelled(runId));
        }
    }
}
