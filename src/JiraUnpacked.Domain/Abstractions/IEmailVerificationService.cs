namespace JiraUnpacked.Domain.Abstractions;

public enum VerificationError
{
    None,
    Invalid,
    TooManyAttempts
}

public sealed record VerificationResult(bool Succeeded, VerificationError Error)
{
    public static VerificationResult Ok() => new(true, VerificationError.None);
    public static VerificationResult Fail(VerificationError error) => new(false, error);
}

public interface IEmailVerificationService
{
    Task<VerificationResult> VerifyAsync(string email, string code, CancellationToken ct = default);
    Task ResendAsync(string email, CancellationToken ct = default);
}
