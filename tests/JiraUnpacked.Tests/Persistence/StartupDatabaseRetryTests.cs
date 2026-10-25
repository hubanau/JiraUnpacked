using System.Net.Sockets;
using JiraUnpacked.Api;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace JiraUnpacked.Tests.Persistence;

/// <summary>
/// Boot-time database work must ride out the database coming up after the api (a Docker daemon
/// restart starts them in no particular order), but must not sit retrying an error that will
/// never go away.
/// </summary>
public sealed class StartupDatabaseRetryTests
{
    [Fact]
    public async Task Retries_while_the_database_host_does_not_resolve_then_succeeds()
    {
        var calls = 0;

        await StartupDatabaseRetry.RunAsync(() =>
        {
            calls++;
            if (calls < 3) throw new SocketException((int)SocketError.HostNotFound);
            return Task.CompletedTask;
        }, NullLogger.Instance, maxAttempts: 5, delay: TimeSpan.Zero);

        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Gives_up_after_the_last_attempt_and_rethrows()
    {
        var calls = 0;

        await Assert.ThrowsAsync<SocketException>(() => StartupDatabaseRetry.RunAsync(() =>
        {
            calls++;
            throw new SocketException((int)SocketError.HostNotFound);
        }, NullLogger.Instance, maxAttempts: 3, delay: TimeSpan.Zero));

        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task A_non_transient_error_fails_on_the_first_attempt()
    {
        var calls = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => StartupDatabaseRetry.RunAsync(() =>
        {
            calls++;
            throw new InvalidOperationException("relation \"Users\" does not exist");
        }, NullLogger.Instance, maxAttempts: 5, delay: TimeSpan.Zero));

        Assert.Equal(1, calls);
    }

    [Fact]
    public void Classifies_connection_failures_as_transient_and_others_not()
    {
        Assert.True(StartupDatabaseRetry.IsTransientConnectFailure(
            new SocketException((int)SocketError.HostNotFound)));
        Assert.True(StartupDatabaseRetry.IsTransientConnectFailure(
            new NpgsqlException("connect failed", new SocketException((int)SocketError.ConnectionRefused))));
        Assert.True(StartupDatabaseRetry.IsTransientConnectFailure(
            new InvalidOperationException("wrapped", new SocketException((int)SocketError.HostNotFound))));

        Assert.False(StartupDatabaseRetry.IsTransientConnectFailure(new NpgsqlException("bad")));
        Assert.False(StartupDatabaseRetry.IsTransientConnectFailure(new InvalidOperationException("bad")));
    }
}
