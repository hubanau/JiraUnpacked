using Npgsql;

namespace JiraUnpacked.Infrastructure.Persistence;

/// <summary>
/// Session settings every app connection carries, applied where the configured connection
/// strings become contexts — not in .env or compose, so no deployment can forget them.
/// </summary>
public static class PostgresSessionOptions
{
    /// <summary>
    /// The connection string with <c>jit=off</c> added to its startup <c>Options</c>, keeping any
    /// options already there. A string that already sets <c>jit</c> is left as it is: an explicit
    /// choice wins.
    ///
    /// Why off: the app's queries are short OLTP reads (a page, a count, a picker), and Postgres
    /// decides to JIT by estimated cost, which large correlated subqueries reach without being
    /// slow. Compiling then costs more than it saves — the history-conditions gate measured a
    /// 50-value `Was` at 1.8–4.6 s with JIT (1.5–2.7 s of it compilation) and 0.9 s without.
    /// </summary>
    public static string WithoutJit(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var options = builder.Options;
        if (options?.Contains("jit=", StringComparison.OrdinalIgnoreCase) == true) return connectionString;
        builder.Options = string.IsNullOrWhiteSpace(options) ? "-c jit=off" : options + " -c jit=off";
        return builder.ConnectionString;
    }
}
