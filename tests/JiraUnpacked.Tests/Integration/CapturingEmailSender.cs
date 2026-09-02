using System.Collections.Concurrent;
using JiraUnpacked.Domain.Abstractions;

namespace JiraUnpacked.Tests.Integration;

/// <summary>Test <see cref="IEmailSender"/> that records the last code and reset link per email.</summary>
public sealed class CapturingEmailSender : IEmailSender
{
    private readonly ConcurrentDictionary<string, string> _codes = new();
    private readonly ConcurrentDictionary<string, string> _resetLinks = new();

    public Task SendVerificationCodeAsync(string email, string code, CancellationToken ct = default)
    {
        _codes[email] = code;
        return Task.CompletedTask;
    }

    public Task SendPasswordResetAsync(string email, string resetLink, CancellationToken ct = default)
    {
        _resetLinks[email] = resetLink;
        return Task.CompletedTask;
    }

    public string? LastCodeFor(string email) => _codes.TryGetValue(email, out var code) ? code : null;

    public string? LastResetLinkFor(string email) =>
        _resetLinks.TryGetValue(email, out var link) ? link : null;

    /// <summary>The token from the captured link — what a user would click through with.</summary>
    public string? LastResetTokenFor(string email)
    {
        var link = LastResetLinkFor(email);
        if (link is null) return null;
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(link).Query);
        return query["token"];
    }
}
