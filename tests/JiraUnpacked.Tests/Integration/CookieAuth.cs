// tests/JiraUnpacked.Tests/Integration/CookieAuth.cs
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;

namespace JiraUnpacked.Tests.Integration;

/// <summary>
/// Helpers for driving the httpOnly refresh cookie explicitly.
///
/// Cookie handling is turned OFF on these clients on purpose. With the default container the
/// browser-like replay hides which token a request actually presented, and the rotation and
/// reuse-rejection tests are precisely about that. Setting the header by hand makes each test
/// state the token it is using.
/// </summary>
internal static class CookieAuth
{
    internal const string CookieName = "ju_rt";

    internal static HttpClient Client(JiraUnpackedApiFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    /// <summary>The refresh token from a response's Set-Cookie, or null if none was set.</summary>
    internal static string? RefreshCookie(HttpResponseMessage response)
    {
        var value = SetCookieHeader(response);
        if (value is null) return null;

        var pair = value.Split(';', 2)[0];
        var token = pair[(pair.IndexOf('=') + 1)..];
        return string.IsNullOrEmpty(token) ? null : token;
    }

    /// <summary>The raw Set-Cookie header for the refresh cookie, attributes included.</summary>
    internal static string? SetCookieHeader(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(v => v.StartsWith($"{CookieName}=", StringComparison.Ordinal))
            : null;

    /// <summary>
    /// A POST carrying the required XHR header and, optionally, the refresh cookie. Both
    /// endpoints are bodyless — the token travels only in the cookie.
    /// </summary>
    internal static Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string? cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        if (cookie is not null) request.Headers.Add("Cookie", $"{CookieName}={cookie}");
        return client.SendAsync(request);
    }

    /// <summary>The same POST WITHOUT the XHR header, for the CSRF-guard tests.</summary>
    internal static Task<HttpResponseMessage> PostWithoutXhrHeaderAsync(
        HttpClient client, string path, string? cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (cookie is not null) request.Headers.Add("Cookie", $"{CookieName}={cookie}");
        return client.SendAsync(request);
    }

    internal static void Authorize(HttpClient client, string accessToken) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
}
