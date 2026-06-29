using System.Net;
using System.Net.Http.Headers;
using System.Text;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Abstractions.Jira;

namespace JiraUnpacked.Infrastructure.Jira;

public sealed partial class JiraApiClient : IJiraApiClient
{
    private readonly HttpClient _http;

    public JiraApiClient(HttpClient http)
    {
        _http = http;
        if (_http.Timeout == Timeout.InfiniteTimeSpan || _http.Timeout > TimeSpan.FromSeconds(30))
            _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task<JiraValidationResult> ValidateAsync(JiraCredentials creds, CancellationToken ct = default)
    {
        try
        {
            using var req = Build(HttpMethod.Get, creds, "/rest/api/3/myself");
            using var resp = await _http.SendAsync(req, ct);
            return resp.StatusCode switch
            {
                HttpStatusCode.OK => JiraValidationResult.Valid,
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => JiraValidationResult.InvalidCredentials,
                _ => JiraValidationResult.Unreachable
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return JiraValidationResult.Unreachable;
        }
    }

    // Builds a request to {baseUrl}{path} with Basic auth + JSON accept.
    // Normalises BaseUrl to a trailing slash so sub-path bases (e.g. https://proxy/jira/)
    // are preserved; strips any leading slash from path so Uri combination is additive.
    private static HttpRequestMessage Build(HttpMethod method, JiraCredentials creds, string path)
    {
        var baseUri = new Uri(creds.BaseUrl.EndsWith("/") ? creds.BaseUrl : creds.BaseUrl + "/", UriKind.Absolute);
        var uri = new Uri(baseUri, path.TrimStart('/'));
        var req = new HttpRequestMessage(method, uri);
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{creds.Email}:{creds.ApiToken}"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return req;
    }
}
