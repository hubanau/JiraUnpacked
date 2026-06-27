using System.Collections.Concurrent;
using JiraUnpacked.Domain.Abstractions;

namespace JiraUnpacked.Tests.Integration;

/// <summary>Test <see cref="IEmailSender"/> that records the last code sent per email.</summary>
public sealed class CapturingEmailSender : IEmailSender
{
    private readonly ConcurrentDictionary<string, string> _codes = new();

    public Task SendVerificationCodeAsync(string email, string code, CancellationToken ct = default)
    {
        _codes[email] = code;
        return Task.CompletedTask;
    }

    public string? LastCodeFor(string email) => _codes.TryGetValue(email, out var code) ? code : null;
}
