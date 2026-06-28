using JiraUnpacked.Domain.Abstractions;

namespace JiraUnpacked.Api.Tenancy;

/// <summary>Resolves the tenant from the request's `endUserId` JWT claim.</summary>
public sealed class HttpContextCurrentTenant : ICurrentTenant
{
    private readonly IHttpContextAccessor _accessor;

    public HttpContextCurrentTenant(IHttpContextAccessor accessor) => _accessor = accessor;

    public Guid? EndUserId =>
        Guid.TryParse(_accessor.HttpContext?.User.FindFirst("endUserId")?.Value, out var id)
            ? id
            : null;
}
