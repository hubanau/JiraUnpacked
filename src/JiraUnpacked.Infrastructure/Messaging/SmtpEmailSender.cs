using System.Net;
using System.Net.Mail;
using JiraUnpacked.Domain.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JiraUnpacked.Infrastructure.Messaging;

/// <summary>
/// Sends the verification code over SMTP. Uses the BCL client rather than adding a dependency,
/// which keeps the project's zero-new-dependency rule intact for a once-per-registration send.
///
/// The code is NEVER logged here. It is a credential: anything that puts it in a log makes
/// every account verifiable by whoever can read logs, which is the flaw the development sender
/// has and the reason that sender is refused outside Development.
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

    public async Task SendVerificationCodeAsync(string email, string code, CancellationToken ct = default)
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
            Subject = "Your Jira Unpacked verification code",
            Body = $"Your verification code is {code}.\n\n"
                   + "It expires shortly. If you did not request it, you can ignore this message.",
            IsBodyHtml = false,
        };
        message.To.Add(email);

        try
        {
            await client.SendMailAsync(message, ct);
        }
        catch (Exception ex) when (ex is SmtpException or InvalidOperationException)
        {
            // Logged WITHOUT the code and without the exception's message, which can echo back
            // the recipient and envelope. The caller already returns an unconditional 202, so a
            // delivery failure must not change what the API tells an unauthenticated caller.
            _logger.LogError("Verification email delivery failed ({ExceptionType}).", ex.GetType().Name);
            throw;
        }
    }
}
