using System.Data.Common;
using JiraUnpacked.Domain.Abstractions;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace JiraUnpacked.Infrastructure.Tenancy;

/// <summary>
/// Sets the `app.current_tenant` Postgres session variable on every connection open,
/// so Row-Level Security filters to the current tenant (or to nothing when unset).
/// Setting it on every open also prevents leakage across pooled connections.
/// </summary>
public sealed class TenantConnectionInterceptor : DbConnectionInterceptor
{
    private readonly ICurrentTenant _tenant;

    public TenantConnectionInterceptor(ICurrentTenant tenant) => _tenant = tenant;

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        => SetTenant(connection);

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        => await SetTenantAsync(connection, cancellationToken);

    private void SetTenant(DbConnection connection)
    {
        using var cmd = CreateCommand(connection);
        cmd.ExecuteNonQuery();
    }

    private async Task SetTenantAsync(DbConnection connection, CancellationToken ct)
    {
        await using var cmd = CreateCommand(connection);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private DbCommand CreateCommand(DbConnection connection)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT set_config('app.current_tenant', @tenant, false)";
        var p = cmd.CreateParameter();
        p.ParameterName = "tenant";
        p.Value = _tenant.EndUserId?.ToString() ?? string.Empty;
        cmd.Parameters.Add(p);
        return cmd;
    }
}
