using System.Security.Cryptography;
using System.Text;
using JiraUnpacked.Domain.Abstractions;
using Microsoft.Extensions.Options;

namespace JiraUnpacked.Infrastructure.Security;

public sealed class AesGcmTokenProtector : ITokenProtector
{
    private const int NonceSize = 12; // AES-GCM standard nonce
    private const int TagSize = 16;
    private readonly byte[] _key;

    public AesGcmTokenProtector(IOptions<TokenProtectionOptions> options)
    {
        var raw = options.Value.Key;
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException("TokenProtection:Key is not configured.");
        byte[] key;
        try { key = Convert.FromBase64String(raw); }
        catch (FormatException) { throw new InvalidOperationException("TokenProtection:Key is not valid base64."); }
        if (key.Length != 32)
            throw new InvalidOperationException("TokenProtection:Key must decode to exactly 32 bytes (AES-256).");
        _key = key;
    }

    public string Protect(string plaintext)
    {
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plain, cipher, tag);

        var combined = new byte[NonceSize + TagSize + cipher.Length];
        Buffer.BlockCopy(nonce, 0, combined, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, combined, NonceSize, TagSize);
        Buffer.BlockCopy(cipher, 0, combined, NonceSize + TagSize, cipher.Length);
        return Convert.ToBase64String(combined);
    }

    public string Unprotect(string protectedValue)
    {
        var combined = Convert.FromBase64String(protectedValue);
        if (combined.Length < NonceSize + TagSize)
            throw new CryptographicException("Ciphertext is too short.");
        var nonce = combined.AsSpan(0, NonceSize);
        var tag = combined.AsSpan(NonceSize, TagSize);
        var cipher = combined.AsSpan(NonceSize + TagSize);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain); // throws CryptographicException on auth failure
        return Encoding.UTF8.GetString(plain);
    }
}
