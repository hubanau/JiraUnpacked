namespace JiraUnpacked.Domain.Security;

public sealed record PasswordValidationResult(bool IsValid, IReadOnlyList<string> Errors);

public sealed class PasswordPolicy
{
    public int MinLength { get; init; } = 12;
    public int MinCharacterClasses { get; init; } = 3;

    public PasswordValidationResult Validate(string? password)
    {
        var errors = new List<string>();
        password ??= string.Empty;

        if (password.Length < MinLength)
            errors.Add($"Password must be at least {MinLength} characters.");

        var classes = 0;
        if (password.Any(char.IsLower)) classes++;
        if (password.Any(char.IsUpper)) classes++;
        if (password.Any(char.IsDigit)) classes++;
        if (password.Any(c => !char.IsLetterOrDigit(c))) classes++;

        if (classes < MinCharacterClasses)
            errors.Add($"Password must contain at least {MinCharacterClasses} character classes (lower, upper, digit, symbol).");

        return new PasswordValidationResult(errors.Count == 0, errors);
    }
}
