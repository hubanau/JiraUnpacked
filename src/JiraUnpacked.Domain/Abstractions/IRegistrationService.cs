namespace JiraUnpacked.Domain.Abstractions;

public enum RegistrationError
{
    None,
    InvalidInviteCode,
    WeakPassword,
    EmailOrUsernameTaken
}

public sealed record RegistrationResult(bool Succeeded, RegistrationError Error, IReadOnlyList<string> Errors)
{
    public static RegistrationResult Ok() => new(true, RegistrationError.None, Array.Empty<string>());
    public static RegistrationResult Fail(RegistrationError error) => new(false, error, Array.Empty<string>());
    public static RegistrationResult Weak(IReadOnlyList<string> errors) => new(false, RegistrationError.WeakPassword, errors);
}

public interface IRegistrationService
{
    Task<RegistrationResult> RegisterAsync(
        string inviteCode, string email, string username, string password, CancellationToken ct = default);
}
