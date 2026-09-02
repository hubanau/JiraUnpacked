namespace JiraUnpacked.Domain.Abstractions;

public interface IEmailSender
{
    Task SendVerificationCodeAsync(string email, string code, CancellationToken ct = default);

    /// <summary>
    /// Sends the reset link. The link is built by the caller from configuration, never from the
    /// request's Host header — see PasswordResetService.
    /// </summary>
    Task SendPasswordResetAsync(string email, string resetLink, CancellationToken ct = default);
}
