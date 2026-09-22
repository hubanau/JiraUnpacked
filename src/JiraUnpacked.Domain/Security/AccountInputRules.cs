using System.Net.Mail;
using System.Text.RegularExpressions;

namespace JiraUnpacked.Domain.Security;

/// <summary>
/// Shape rules for the email and username a new account is registered with. They exist for
/// more than tidiness: login decides which column to match from the input alone — an '@'
/// means email, anything else a username — so a username must never be able to look like an
/// email. Limits sit inside the Users column sizes (Email 256, Username 64).
/// The SPA mirrors these in src/JiraUnpacked.Web/src/auth/accountInput.ts.
/// </summary>
public static partial class AccountInputRules
{
    /// <summary>RFC 5321's practical limit on a forward path.</summary>
    public const int EmailMaxLength = 254;

    public const int UsernameMinLength = 3;
    public const int UsernameMaxLength = 32;

    // \z, not $: in .NET '$' also matches before a trailing newline, which would admit "alice\n".
    [GeneratedRegex(@"^[A-Za-z0-9._-]{3,32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex UsernamePattern();

    /// <summary>
    /// True when <paramref name="email"/>, trimmed, is a bare address of at most
    /// <see cref="EmailMaxLength"/> characters. A display-name form ("Alice &lt;a@x.com&gt;")
    /// parses as a MailAddress but its Address differs from the input, so it is rejected.
    /// </summary>
    public static bool IsValidEmail(string? email)
    {
        var trimmed = (email ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed.Length > EmailMaxLength) return false;

        try
        {
            return string.Equals(new MailAddress(trimmed).Address, trimmed, StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>3–32 characters of letters, digits, '.', '_' or '-'. Never '@', never whitespace.</summary>
    public static bool IsValidUsername(string? username) =>
        username is not null && UsernamePattern().IsMatch(username);
}
