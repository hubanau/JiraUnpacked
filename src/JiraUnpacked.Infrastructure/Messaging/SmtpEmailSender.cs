using System.Net;
using System.Net.Mail;
using JiraUnpacked.Domain.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JiraUnpacked.Infrastructure.Messaging;

/// <summary>
/// Sends verification codes and password-reset links over SMTP. Uses the BCL client rather than
/// adding a dependency, which keeps the project's zero-new-dependency rule intact for a couple
/// of sends per account lifetime.
///
/// Neither the code nor the reset link is EVER logged here. Both are credentials: anything that
/// puts one in a log lets whoever can read logs verify or take over an account. That is exactly
/// the flaw the development sender has, and the reason it is refused outside Development.
/// </summary>
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly EmailOptions _options;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(IOptions<EmailOptions> options, ILogger<SmtpEmailSender> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public Task SendVerificationCodeAsync(string email, string code, CancellationToken ct = default) =>
        SendAsync(
            email,
            "Your Jira Unpacked verification code",
            $"Your verification code is {code}."
            + Environment.NewLine + Environment.NewLine
            + "It expires shortly. If you did not request it, you can ignore this message.",
            ct);

    public Task SendPasswordResetAsync(string email, string resetLink, CancellationToken ct = default) =>
        SendAsync(
            email,
            "Reset your Jira Unpacked password",
            "Use the link below to choose a new password."
            + Environment.NewLine + Environment.NewLine
            + resetLink
            + Environment.NewLine + Environment.NewLine
            // Says explicitly that nothing has changed yet. Someone who receives this without
            // asking should be able to ignore it without wondering whether they must act.
            + "The link expires shortly and can be used once. If you did not ask to reset your "
            + "password, ignore this message — your password has not changed.",
            ct);

    private async Task SendAsync(string email, string subject, string body, CancellationToken ct)
    {
        using var client = new SmtpClient(_options.Host, _options.Port)
        {
            EnableSsl = _options.UseStartTls,
            DeliveryMethod = SmtpDeliveryMethod.Network,
        };

        if (!string.IsNullOrWhiteSpace(_options.Username))
        {
            client.Credentials = new NetworkCredential(_options.Username, _options.Password);
        }

        using var message = new MailMessage
        {
            From = new MailAddress(_options.FromAddress, _options.FromName),
            Subject = subject,
            Body = body,
            IsBodyHtml = false,
        };
        message.To.Add(email);

        try
        {
            await client.SendMailAsync(message, ct);
        }
        catch (Exception ex) when (ex is SmtpException or InvalidOperationException)
        {
            // Logged WITHOUT the body — it carries the code or the reset link — and without the
            // exception's message, which can echo back the recipient and envelope. Callers
            // answer unconditionally, so a delivery failure must not change what an
            // unauthenticated caller is told.
            _logger.LogError("Email delivery failed ({ExceptionType}).", ex.GetType().Name);
            throw;
        }
    }
}
