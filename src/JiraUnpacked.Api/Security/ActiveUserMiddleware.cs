using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Api.Security;

/// <summary>
/// FR-A5: a blocked (or deleted) user loses access immediately, not when their access token
/// expires. Blocking revokes refresh tokens, but a JWT stays cryptographically valid for its
/// whole lifetime, so without this check a blocked user keeps every endpoint — including
/// "Sync now" — for up to 15 minutes.
///
/// For every authenticated request to an endpoint that requires authorization, the user named
/// by the token is looked up by primary key and the request is answered 401 unless the account
/// still exists and is Active. Anonymous endpoints (login, refresh, register, health, the SPA
/// shell) are untouched, so a stale token in the browser can never lock anyone out of signing
/// in again.
/// </summary>
public sealed class ActiveUserMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IServiceScopeFactory _scopes;

    public ActiveUserMiddleware(RequestDelegate next, IServiceScopeFactory scopes)
    {
        _next = next;
        _scopes = scopes;
    }

    public async Task Invoke(HttpContext context)
    {
        if (!RequiresActiveUser(context))
        {
            await _next(context);
            return;
        }

        var claim = context.User.FindFirst("sub")?.Value ?? context.User.FindFirst("endUserId")?.Value;
        if (!Guid.TryParse(claim, out var userId) || !await IsActiveAsync(userId, context.RequestAborted))
        {
            await RejectAsync(context);
            return;
        }

        await _next(context);
    }

    private static bool RequiresActiveUser(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated != true) return false;

        var endpoint = context.GetEndpoint();
        if (endpoint is null) return false;
        if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null) return false;
        return endpoint.Metadata.GetMetadata<IAuthorizeData>() is not null;
    }

    /// <summary>
    /// Deliberately a CHILD scope's context, never the request's. AppDbContext captures the
    /// tenant id when it is constructed, and this runs before TenantScopeMiddleware opens the
    /// tenant scope: resolving the request-scoped context here would pin it to "no tenant" and
    /// every tenant-owned read later in the request would come back empty. Users itself is not
    /// tenant-filtered (it is the tenant root), so the lookup needs no scope.
    /// </summary>
    private async Task<bool> IsActiveAsync(Guid userId, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var status = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => (UserStatus?)u.Status)
            .FirstOrDefaultAsync(ct);
        return status == UserStatus.Active;
    }

    private static Task RejectAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer error=\"invalid_token\"";
        return context.Response.WriteAsJsonAsync(
            new ProblemDetails
            {
                Status = StatusCodes.Status401Unauthorized,
                Title = "This account is no longer active.",
            },
            options: null,
            contentType: "application/problem+json");
    }
}
