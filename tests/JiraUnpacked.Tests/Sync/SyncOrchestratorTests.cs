using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Abstractions.Jira;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Sync;
using JiraUnpacked.Infrastructure.Tenancy;
using JiraUnpacked.Tests.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace JiraUnpacked.Tests.Sync;

public class SyncOrchestratorTests
{
    // Passes the token through unchanged — the orchestrator only needs Unprotect to return something.
    private sealed class PassthroughProtector : ITokenProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }

    // Jira client whose only exercised member is the issue search; the rest are unused by RunProjectAsync.
    private sealed class StubJiraClient : IJiraApiClient
    {
        private readonly Func<Task<SearchPage>> _search;
        public StubJiraClient(Func<Task<SearchPage>> search) => _search = search;

        public Task<SearchPage> SearchIssuesPageAsync(JiraCredentials creds, string jql, string? nextPageToken, CancellationToken ct = default) => _search();
        public Task<JiraValidationResult> ValidateAsync(JiraCredentials creds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<JiraField>> GetFieldsAsync(JiraCredentials creds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<JiraProjectSummary>> GetProjectsAsync(JiraCredentials creds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<JiraProjectSummary?> GetProjectAsync(JiraCredentials creds, string projectKey, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<JiraStatus>> GetStatusesAsync(JiraCredentials creds, string projectKey, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<JqlValidationResult> ParseJqlAsync(JiraCredentials creds, string jql, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChangelogPage> GetIssueChangelogPageAsync(JiraCredentials creds, string jiraIssueId, int startAt, CancellationToken ct = default) => throw new NotSupportedException();
    }

    // These failure tests never reach the transform phase (they throw during extract), so no-op stubs suffice.
    private sealed class NoopIngest : IChangelogIngestService
    {
        public Task IngestProjectAsync(Guid projectId, JiraCredentials creds, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoopDerivation : IDerivationService
    {
        public Task DeriveProjectAsync(Guid projectId, CancellationToken ct = default) => Task.CompletedTask;
    }

    // Simulates an infra failure mid-sync: throws a DbUpdateException (carrying raw SQL detail) on the Nth save.
    private sealed class FailingSaveDbContext : AppDbContext
    {
        private readonly int _throwOnCall;
        private int _calls;
        public FailingSaveDbContext(DbContextOptions<AppDbContext> options, ICurrentTenant tenant, int throwOnCall)
            : base(options, tenant) => _throwOnCall = throwOnCall;

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (++_calls == _throwOnCall)
                throw new DbUpdateException(
                    "23505: duplicate key value violates unique constraint \"IX_RawIssueSnapshots_EndUserId_JiraIssueId\"");
            return base.SaveChangesAsync(cancellationToken);
        }
    }

    private static DbContextOptions<AppDbContext> InMemoryOptions() =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString(), new InMemoryDatabaseRoot())
            .Options;

    private static (Guid projectId, Guid connectionId) Seed(DbContextOptions<AppDbContext> options, ICurrentTenant tenant)
    {
        var projectId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        using var db = new AppDbContext(options, tenant);
        db.JiraConnections.Add(new JiraConnection
        {
            Id = connectionId, BaseUrl = "https://acme.atlassian.net", Email = "ops@acme.com", ApiTokenEncrypted = "enc"
        });
        db.JiraProjects.Add(new JiraProject
        {
            Id = projectId, JiraConnectionId = connectionId,
            JiraProjectKey = "ACME", JiraProjectId = "1", SyncIntervalMinutes = 60
        });
        db.JiraQueries.Add(new JiraQuery { Id = Guid.NewGuid(), JiraProjectId = projectId, Jql = "project = ACME" });
        db.SaveChanges();
        return (projectId, connectionId);
    }

    [Fact]
    public async Task Unexpected_infra_failure_persists_a_generic_error_not_raw_detail()
    {
        var tenant = new AmbientCurrentTenant();
        using var _ = tenant.BeginScope(Guid.NewGuid());
        var options = InMemoryOptions();
        var (projectId, _) = Seed(options, tenant);

        var jira = new StubJiraClient(() =>
            Task.FromResult(new SearchPage(new[] { new RawIssue("10001", "{}") }, null)));

        // throwOnCall: 2 => save #1 inserts the Running run, save #2 (the fetched page) throws the infra error.
        await using var db = new FailingSaveDbContext(options, tenant, throwOnCall: 2);
        var orchestrator = new SyncOrchestrator(db, jira, new PassthroughProtector(),
            new FakeClock(DateTime.UtcNow), new NoopIngest(), new NoopDerivation(), NullLogger<SyncOrchestrator>.Instance);

        var result = await orchestrator.RunProjectAsync(projectId);

        Assert.Equal("Failed", result.Run!.Status);
        Assert.Equal("Sync failed unexpectedly.", result.Run.Error); // no SQL fragments / column names leaked
    }

    [Fact]
    public async Task Jira_failure_persists_its_own_safe_message()
    {
        var tenant = new AmbientCurrentTenant();
        using var _ = tenant.BeginScope(Guid.NewGuid());
        var options = InMemoryOptions();
        var (projectId, _) = Seed(options, tenant);

        var jira = new StubJiraClient(() =>
            Task.FromException<SearchPage>(new JiraApiException(JiraErrorKind.Upstream, "Jira upstream error (500).")));

        await using var db = new AppDbContext(options, tenant);
        var orchestrator = new SyncOrchestrator(db, jira, new PassthroughProtector(),
            new FakeClock(DateTime.UtcNow), new NoopIngest(), new NoopDerivation(), NullLogger<SyncOrchestrator>.Instance);

        var result = await orchestrator.RunProjectAsync(projectId);

        Assert.Equal("Failed", result.Run!.Status);
        Assert.Equal("Jira upstream error (500).", result.Run.Error); // JiraApiException messages stay intact
    }
}
