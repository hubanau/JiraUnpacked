using JiraUnpacked.Infrastructure.Security;

namespace JiraUnpacked.Tests.Security;

public class BcryptPasswordHasherTests
{
    private readonly BcryptPasswordHasher _hasher = new();

    [Fact]
    public void Hash_DoesNotReturnPlaintext()
    {
        var hash = _hasher.Hash("Correct-Horse-9!");
        Assert.NotEqual("Correct-Horse-9!", hash);
        Assert.StartsWith("$2", hash); // bcrypt format marker
    }

    [Fact]
    public void Verify_ReturnsTrue_ForCorrectPassword()
    {
        var hash = _hasher.Hash("Correct-Horse-9!");
        Assert.True(_hasher.Verify("Correct-Horse-9!", hash));
    }

    [Fact]
    public void Verify_ReturnsFalse_ForWrongPassword()
    {
        var hash = _hasher.Hash("Correct-Horse-9!");
        Assert.False(_hasher.Verify("wrong-password", hash));
    }
}
