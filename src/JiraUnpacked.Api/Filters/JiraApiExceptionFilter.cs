using JiraUnpacked.Domain.Abstractions.Jira;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace JiraUnpacked.Api.Filters;

public sealed class JiraApiExceptionFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not JiraApiException ex) return;
        var status = ex.Kind == JiraErrorKind.Unauthorized ? 401 : 502;
        context.Result = new ObjectResult(new ProblemDetails
        {
            Status = status,
            Title = ex.Kind == JiraErrorKind.Unauthorized
                ? "Jira rejected the stored credentials."
                : "Jira could not be reached."
        }) { StatusCode = status };
        context.ExceptionHandled = true;
    }
}
