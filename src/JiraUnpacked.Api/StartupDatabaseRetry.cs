using System.Net.Sockets;
using Npgsql;

namespace JiraUnpacked.Api;

/// <summary>
/// Retries the boot-time database work while the database is briefly unreachable, instead of
/// crashing on the first attempt.
///
/// After a Docker daemon restart nothing orders the containers: the api can start before
/// Postgres is accepting connections (or before its DNS name exists at all), and the seeder's
/// first query then kills the process. The restart policy recovers from that anyway; this just
/// turns a crash loop into a few warnings. Only connection-level failures are retried — a wrong
/// password or a missing table is not going to fix itself and still fails immediately.
/// </summary>
public static class StartupDatabaseRetry
{
    public static async Task RunAsync(
        Func<Task> action,
        ILogger logger,
        int maxAttempts = 30,
        TimeSpan? delay = null,
        CancellationToken ct = default)
    {
        var wait = delay ?? TimeSpan.FromSeconds(2);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await action();
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts && IsTransientConnectFailure(ex))
            {
                logger.LogWarning(
                    "Database not reachable at startup ({ExceptionType}: {Message}); attempt {Attempt}/{MaxAttempts}, retrying in {Delay}.",
                    ex.GetType().Name, ex.Message, attempt, maxAttempts, wait);
                await Task.Delay(wait, ct);
            }
        }
    }

    /// <summary>
    /// A host that does not resolve surfaces as a bare SocketException (Npgsql does not wrap
    /// it); "the database system is starting up" is a PostgresException that Npgsql flags as
    /// transient. Authentication and schema errors are not transient and are not retried.
    /// </summary>
    public static bool IsTransientConnectFailure(Exception ex) => ex switch
    {
        SocketException => true,
        NpgsqlException { IsTransient: true } => true,
        _ => ex.InnerException is not null && IsTransientConnectFailure(ex.InnerException),
    };
}
