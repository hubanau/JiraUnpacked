// tests/JiraUnpacked.Tests/Security/AesGcmTokenProtectorTests.cs
using System.Security.Cryptography;
using JiraUnpacked.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace JiraUnpacked.Tests.Security;

public class AesGcmTokenProtectorTests
{
    private static AesGcmTokenProtector New(out byte[] key)
    {
        key = RandomNumberGenerator.GetBytes(32);
        var opts = Options.Create(new TokenProtectionOptions { Key = Convert.ToBase64String(key) });
        return new AesGcmTokenProtector(opts);
    }

    [Fact]
    public void Protect_then_Unprotect_round_trips()
    {
        var p = New(out _);
        var cipher = p.Protect("super-secret-token");
        Assert.NotEqual("super-secret-token", cipher);
        Assert.Equal("super-secret-token", p.Unprotect(cipher));
    }

    [Fact]
    public void Protect_uses_a_fresh_nonce_each_call()
    {
        var p = New(out _);
        Assert.NotEqual(p.Protect("x"), p.Protect("x"));
    }

    [Fact]
    public void Unprotect_fails_on_tampered_ciphertext()
    {
        var p = New(out _);
        var bytes = Convert.FromBase64String(p.Protect("x"));
        bytes[^1] ^= 0xFF; // flip a ciphertext byte
        Assert.ThrowsAny<CryptographicException>(() => p.Unprotect(Convert.ToBase64String(bytes)));
    }

    [Fact]
    public void Unprotect_fails_with_a_different_key()
    {
        var a = New(out _);
        var cipher = a.Protect("x");
        var b = New(out _); // different random key
        Assert.ThrowsAny<CryptographicException>(() => b.Unprotect(cipher));
    }

    [Fact]
    public void Ctor_rejects_a_key_that_is_not_32_bytes()
    {
        var opts = Options.Create(new TokenProtectionOptions { Key = Convert.ToBase64String(new byte[16]) });
        Assert.Throws<InvalidOperationException>(() => new AesGcmTokenProtector(opts));
    }
}
