using JiraUnpacked.Domain.Abstractions;
using Microsoft.Extensions.Logging;

namespace JiraUnpacked.Infrastructure.Messaging;

/// <summary>
/// Development email sender — logs the verification code instead of sending it.
/// A real provider (AWS SES/SMTP) replaces this in a later phase.
/// </summary>
public sealed class LoggingEmailSender : IEmailSender
{
    private readonly ILogger<LoggingEmailSender> _logger;

    public LoggingEmailSender(ILogger<LoggingEmailSender> logger) => _logger = logger;

    public Task SendVerificationCodeAsync(string email, string code, CancellationToken ct = default)
    {
        _logger.LogInformation("Verification code for {Email}: {Code}", email, code);
        return Task.CompletedTask;
    }

    public Task SendPasswordResetAsync(string email, string resetLink, CancellationToken ct = default)
    {
        // Logging the link is the same exposure as logging a verification code — anyone who can
        // read logs can take the account. Acceptable ONLY because this sender is refused outside
        // Development by ProductionConfigGate.
        _logger.LogInformation("Password reset link for {Email}: {ResetLink}", email, resetLink);
        return Task.CompletedTask;
    }
}
