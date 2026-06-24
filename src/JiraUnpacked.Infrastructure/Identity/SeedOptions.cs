namespace JiraUnpacked.Infrastructure.Identity;

public sealed class SeedAdmin
{
    public string Email { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public sealed class SeedOptions
{
    public const string SectionName = "Seed";

    public List<SeedAdmin> Admins { get; set; } = new();
}
