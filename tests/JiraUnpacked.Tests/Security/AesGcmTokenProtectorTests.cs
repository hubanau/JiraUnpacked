// tests/JiraUnpacked.Tests/Security/AesGcmTokenProtectorTests.cs
using System.Security.Cryptography;
using System.Text;
using JiraUnpacked.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace JiraUnpacked.Tests.Security;

public class AesGcmTokenProtectorTests
{
    private static AesGcmTokenProtector New(out byte[] key)
    {
        key = RandomNumberGenerator.GetBytes(32);
        return Using(key);
    }

    private static AesGcmTokenProtector Using(byte[] key) =>
        new(Options.Create(new TokenProtectionOptions { Key = Convert.ToBase64String(key) }));

    private static byte[] Envelope(string protectedValue) =>
        Convert.FromBase64String(protectedValue[AesGcmTokenProtector.EnvelopePrefix.Length..]);

    /// <summary>
    /// Reproduces the pre-envelope format exactly — the token sealed directly under the master
    /// key, raw base64 with no prefix — so the compatibility test is checking against the real
    /// old layout rather than something this file invented.
    /// </summary>
    private static string LegacyProtect(byte[] masterKey, string plaintext)
    {
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(masterKey, 16);
        aes.Encrypt(nonce, plain, cipher, tag);

        var combined = new byte[12 + 16 + cipher.Length];
        Buffer.BlockCopy(nonce, 0, combined, 0, 12);
        Buffer.BlockCopy(tag, 0, combined, 12, 16);
        Buffer.BlockCopy(cipher, 0, combined, 12 + 16, cipher.Length);
        return Convert.ToBase64String(combined);
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
    public void Protect_writes_the_version_prefix()
    {
        var p = New(out _);

        // The prefix is what lets a future KMS-wrapped format be a new tag rather than a
        // second migration, and what distinguishes an envelope from a legacy value.
        Assert.StartsWith("v1:", p.Protect("x"));
    }

    [Fact]
    public void Every_token_gets_its_own_data_key()
    {
        var p = New(out _);

        var first = Envelope(p.Protect("same-plaintext"));
        var second = Envelope(p.Protect("same-plaintext"));

        // The wrapped data key occupies the first 60 bytes (nonce | tag | key). Two encryptions
        // of identical plaintext must differ THERE, not merely in the payload — if they matched,
        // one data key would be covering many messages and AES-GCM's nonce-reuse limits would
        // start to apply across the whole table instead of per token.
        Assert.NotEqual(first[..60], second[..60]);
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
        var bytes = Envelope(p.Protect("x"));
        bytes[^1] ^= 0xFF; // flip a payload byte
        Assert.ThrowsAny<CryptographicException>(
            () => p.Unprotect("v1:" + Convert.ToBase64String(bytes)));
    }

    [Fact]
    public void Unprotect_fails_when_the_wrapped_data_key_is_tampered_with()
    {
        var p = New(out _);
        var bytes = Envelope(p.Protect("x"));
        bytes[0] ^= 0xFF; // flip a byte of the wrapped key's nonce

        // The wrap is authenticated too — corrupting it must fail rather than yield a garbage
        // data key that then produces a garbage plaintext.
        Assert.ThrowsAny<CryptographicException>(
            () => p.Unprotect("v1:" + Convert.ToBase64String(bytes)));
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
    public void A_legacy_value_still_decrypts_under_the_same_master_key()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var legacy = LegacyProtect(key, "token-from-before-the-change");

        // Load-bearing: without this the format change would be a flag day that stranded every
        // stored connection, rather than a background upgrade.
        Assert.Equal("token-from-before-the-change", Using(key).Unprotect(legacy));
    }

    [Fact]
    public void Legacy_and_current_values_are_distinguishable()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var legacy = LegacyProtect(key, "old");
        var current = Using(key).Protect("new");

        // A legacy value is raw base64 whose first byte is a random nonce, so no BYTE prefix
        // could separate the two. ':' is outside the base64 alphabet, which is what makes the
        // marker unambiguous against anything the old format could ever have produced.
        Assert.True(AesGcmTokenProtector.IsLegacy(legacy));
        Assert.False(AesGcmTokenProtector.IsLegacy(current));
        Assert.DoesNotContain(":", legacy, StringComparison.Ordinal);
    }

    [Fact]
    public void Rewrapping_a_legacy_value_preserves_the_token()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var protector = Using(key);
        var legacy = LegacyProtect(key, "jira-api-token");

        // Exactly what TokenEnvelopeUpgrader does to each stale row.
        var upgraded = protector.Protect(protector.Unprotect(legacy));

        Assert.False(AesGcmTokenProtector.IsLegacy(upgraded));
        Assert.Equal("jira-api-token", protector.Unprotect(upgraded));
    }

    [Fact]
    public void Ctor_rejects_a_key_that_is_not_32_bytes()
    {
        var opts = Options.Create(new TokenProtectionOptions { Key = Convert.ToBase64String(new byte[16]) });
        Assert.Throws<InvalidOperationException>(() => new AesGcmTokenProtector(opts));
    }
}
