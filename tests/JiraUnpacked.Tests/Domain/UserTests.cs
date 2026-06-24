using JiraUnpacked.Domain.Entities;

namespace JiraUnpacked.Tests.Domain;

public class UserTests
{
    [Theory]
    [InlineData("Alice@Example.com", "alice@example.com")]
    [InlineData("  USER@EXAMPLE.COM  ", "user@example.com")]
    [InlineData("already@normal.com", "already@normal.com")]
    public void NormalizeEmail_LowercasesAndTrims(string input, string expected)
    {
        Assert.Equal(expected, User.NormalizeEmail(input));
    }

    [Fact]
    public void NormalizeEmail_DifferentCasing_NormalizesToSameValue()
    {
        Assert.Equal(User.NormalizeEmail("Bob@Example.com"), User.NormalizeEmail("bob@EXAMPLE.com"));
    }
}
