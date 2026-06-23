namespace JiraUnpacked.Infrastructure.Security;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "JiraUnpacked";
    public string Audience { get; set; } = "JiraUnpacked";

    /// <summary>HMAC signing key; must be at least 32 characters (256 bits) for HS256.</summary>
    public string SigningKey { get; set; } = string.Empty;

    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 7;
}
