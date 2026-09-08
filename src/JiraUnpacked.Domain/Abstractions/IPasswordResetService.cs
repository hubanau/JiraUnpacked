namespace JiraUnpacked.Domain.Abstractions;

/// <summary>
/// Why a reset was refused. Deliberately coarse: the request half never reports anything at
/// all, and the redeem half must not distinguish "no such token" from "expired" or "already
/// used" — each of those is a fact about somebody else's account.
/// </summary>
public enum PasswordResetError { None, InvalidToken, WeakPassword }

public sealed record PasswordResetResult(PasswordResetError Error, IReadOnlyList<string> Errors)
{
    public static PasswordResetResult Ok() => new(PasswordResetError.None, Array.Empty<string>());

    public static PasswordResetResult InvalidToken() =>
        new(PasswordResetError.InvalidToken, Array.Empty<string>());

    public static PasswordResetResult Weak(IReadOnlyList<string> errors) =>
        new(PasswordResetError.WeakPassword, errors);
}

public interface IPasswordResetService
{
    /// <summary>
    /// Issues a reset link if — and only if — the address belongs to an Active user. Returns
    /// nothing either way: the caller answers 202 unconditionally, because any observable
    /// difference turns this into an account-existence oracle.
    /// </summary>
    Task RequestAsync(string email, CancellationToken ct = default);

    /// <summary>
    /// Redeems a token and sets the new password. On success every refresh token for the user
    /// is revoked — a reset prompted by someone else having access is pointless if their
    /// session survives it.
    /// </summary>
    Task<PasswordResetResult> ResetAsync(string token, string newPassword, CancellationToken ct = default);
}
