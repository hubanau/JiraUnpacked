namespace JiraUnpacked.Domain.Abstractions.Jira;

public enum JiraErrorKind { Unauthorized, NotFound, RateLimited, Upstream }

public sealed class JiraApiException : Exception
{
    public JiraErrorKind Kind { get; }
    public JiraApiException(JiraErrorKind kind, string message) : base(message) => Kind = kind;
}
