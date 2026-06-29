namespace JiraUnpacked.Infrastructure.Security;

public sealed class TokenProtectionOptions
{
    public const string SectionName = "TokenProtection";
    public string? Key { get; set; } // base64 of a 32-byte key
}
