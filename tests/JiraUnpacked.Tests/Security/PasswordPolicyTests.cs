using JiraUnpacked.Domain.Security;

namespace JiraUnpacked.Tests.Security;

public class PasswordPolicyTests
{
    private readonly PasswordPolicy _policy = new(); // defaults: min 12, >=3 classes

    [Fact]
    public void Validate_Accepts_StrongPassword()
    {
        var result = _policy.Validate("Correct-Horse-9!");
        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_Rejects_TooShort()
    {
        var result = _policy.Validate("Ab1!");
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("12"));
    }

    [Fact]
    public void Validate_Rejects_TooFewCharacterClasses()
    {
        var result = _policy.Validate("alllowercaseletters"); // 1 class, long enough
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("character"));
    }
}
