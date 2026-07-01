using JiraUnpacked.Domain.Abstractions;

namespace JiraUnpacked.Api.Tenancy;

/// <summary>Opens the ambient tenant scope for the request from the `endUserId` JWT claim.</summary>
public sealed class TenantScopeMiddleware
{
    private readonly RequestDelegate _next;
    public TenantScopeMiddleware(RequestDelegate next) => _next = next;

    public async Task Invoke(HttpContext context, ITenantScope tenantScope)
    {
        var claim = context.User.FindFirst("endUserId")?.Value;
        if (Guid.TryParse(claim, out var endUserId))
        {
            using (tenantScope.BeginScope(endUserId))
                await _next(context);
        }
        else
        {
            await _next(context);
        }
    }
}
