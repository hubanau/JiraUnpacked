using JiraUnpacked.Domain.Abstractions.Jira;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace JiraUnpacked.Api.Filters;

public sealed class JiraApiExceptionFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not JiraApiException ex) return;

        // Every upstream Jira failure is 502 — including a rejected credential.
        // It used to be 401, which the SPA could not tell apart from its own JWT
        // expiring, so a revoked *Jira* token signed the user out of the app.
        // 401 now means exactly one thing: our session. The title still separates
        // "your stored token is wrong" from "Jira is unreachable".
        const int status = StatusCodes.Status502BadGateway;
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
