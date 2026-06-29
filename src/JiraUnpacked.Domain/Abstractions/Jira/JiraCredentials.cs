namespace JiraUnpacked.Domain.Abstractions.Jira;

public sealed record JiraCredentials(string BaseUrl, string Email, string ApiToken)
{
    public override string ToString() => $"JiraCredentials {{ BaseUrl = {BaseUrl}, Email = {Email}, ApiToken = *** }}";
}
