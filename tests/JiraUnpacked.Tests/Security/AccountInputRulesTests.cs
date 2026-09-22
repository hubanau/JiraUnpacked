using JiraUnpacked.Domain.Security;

namespace JiraUnpacked.Tests.Security;

public sealed class AccountInputRulesTests
{
    [Theory]
    [InlineData("alice@example.com")]
    [InlineData("Alice.Smith+jira@Example.co.uk")]
    [InlineData("  padded@example.com  ")] // validated trimmed; stored normalized
    public void IsValidEmail_Accepts_PlainAddresses(string email) =>
        Assert.True(AccountInputRules.IsValidEmail(email));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-email")]
    [InlineData("alice@")]
    [InlineData("@example.com")]
    [InlineData("Alice <alice@example.com>")]
    [InlineData("a@b.com, c@d.com")]
    public void IsValidEmail_Rejects_NonAddresses(string? email) =>
        Assert.False(AccountInputRules.IsValidEmail(email));

    [Fact]
    public void IsValidEmail_EnforcesTheLengthLimit()
    {
        var atLimit = new string('a', AccountInputRules.EmailMaxLength - "@example.com".Length) + "@example.com";
        Assert.Equal(AccountInputRules.EmailMaxLength, atLimit.Length);
        Assert.True(AccountInputRules.IsValidEmail(atLimit));
        Assert.False(AccountInputRules.IsValidEmail("a" + atLimit));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("alice.smith")]
    [InlineData("Bob_the-Builder.2")]
    [InlineData("abcdefghijklmnopqrstuvwxyz012345")] // exactly 32
    public void IsValidUsername_Accepts(string username) =>
        Assert.True(AccountInputRules.IsValidUsername(username));

    [Theory]
    [InlineData(null)]
    [InlineData("ab")]
    [InlineData("alice@example.com")]
    [InlineData("has space")]
    [InlineData(" alice")]
    [InlineData("alice\n")]
    [InlineData("bang!")]
    [InlineData("naïve")]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456")] // 33
    public void IsValidUsername_Rejects(string? username) =>
        Assert.False(AccountInputRules.IsValidUsername(username));
}
