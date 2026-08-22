using System.Security.Cryptography;
using System.Text;
using JiraUnpacked.Domain.Abstractions;
using Microsoft.Extensions.Options;

namespace JiraUnpacked.Infrastructure.Security;

/// <summary>
/// Envelope encryption for stored Jira API tokens.
///
/// Each token gets its own randomly generated 256-bit DATA key; the data key is then wrapped
/// with the master key from configuration. Two things fall out of that:
///
/// - Rotating the master key re-wraps 32-byte data keys instead of re-encrypting every
///   ciphertext, so rotation stops being proportional to the data.
/// - A data key encrypts exactly ONE message, so AES-GCM nonce reuse — the failure mode that
///   makes GCM catastrophic rather than merely broken — cannot arise from volume at all.
///
/// What this does NOT do, and the reason issue #79 stays open: the master key still lives in
/// this process, so one leaked configuration value still decrypts everything, and there is no
/// audit trail of decrypts. Both require an external KMS by definition. This is the half of
/// that requirement achievable locally, and the format is versioned so the other half becomes
/// a new version tag rather than a second migration.
/// </summary>
public sealed class AesGcmTokenProtector : ITokenProtector
{
    private const int NonceSize = 12;   // AES-GCM standard nonce
    private const int TagSize = 16;
    private const int KeySize = 32;     // AES-256

    /// <summary>
    /// Version marker for the envelope format. Deliberately NOT a leading byte: a legacy value
    /// is raw base64, whose first byte is a random nonce and could be any value, so no byte
    /// prefix can distinguish the two. ':' is outside the base64 alphabet, which makes this
    /// prefix unambiguous against anything the previous format could ever have produced.
    /// </summary>
    public const string EnvelopePrefix = "v1:";

    /// <summary>wrapNonce | wrapTag | wrappedDataKey — everything before the payload.</summary>
    private const int HeaderSize = NonceSize + TagSize + KeySize;

    private readonly byte[] _masterKey;

    public AesGcmTokenProtector(IOptions<TokenProtectionOptions> options)
    {
        _masterKey = ReadKey(options.Value.Key);
    }

    private static byte[] ReadKey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException("TokenProtection:Key is not configured.");

        byte[] key;
        try { key = Convert.FromBase64String(raw); }
        catch (FormatException) { throw new InvalidOperationException("TokenProtection:Key is not valid base64."); }

        if (key.Length != KeySize)
            throw new InvalidOperationException("TokenProtection:Key must decode to exactly 32 bytes (AES-256).");

        return key;
    }

    public string Protect(string plaintext)
    {
        var dataKey = RandomNumberGenerator.GetBytes(KeySize);
        try
        {
            var payload = Seal(dataKey, Encoding.UTF8.GetBytes(plaintext));
            var wrapped = Seal(_masterKey, dataKey);

            var envelope = new byte[wrapped.Length + payload.Length];
            Buffer.BlockCopy(wrapped, 0, envelope, 0, wrapped.Length);
            Buffer.BlockCopy(payload, 0, envelope, wrapped.Length, payload.Length);

            return EnvelopePrefix + Convert.ToBase64String(envelope);
        }
        finally
        {
            // The wrapped copy is what persists; this one has no reason to linger in the heap.
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    public string Unprotect(string protectedValue)
    {
        if (!protectedValue.StartsWith(EnvelopePrefix, StringComparison.Ordinal))
        {
            // Written before envelope encryption: the token was sealed directly under the
            // master key. Still readable so an upgrade can be a background pass rather than a
            // flag day. TokenEnvelopeUpgrader rewrites these; see IsLegacy.
            return Encoding.UTF8.GetString(Open(_masterKey, Convert.FromBase64String(protectedValue)));
        }

        var envelope = Convert.FromBase64String(protectedValue[EnvelopePrefix.Length..]);
        if (envelope.Length < HeaderSize + NonceSize + TagSize)
            throw new CryptographicException("Token envelope is too short.");

        var dataKey = Open(_masterKey, envelope.AsSpan(0, HeaderSize).ToArray());
        try
        {
            return Encoding.UTF8.GetString(Open(dataKey, envelope.AsSpan(HeaderSize).ToArray()));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    /// <summary>True for a value stored before envelope encryption, so it can be rewritten.</summary>
    public static bool IsLegacy(string protectedValue) =>
        !protectedValue.StartsWith(EnvelopePrefix, StringComparison.Ordinal);

    /// <summary>nonce | tag | ciphertext under the given key.</summary>
    private static byte[] Seal(byte[] key, byte[] plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plain, cipher, tag);

        var sealed_ = new byte[NonceSize + TagSize + cipher.Length];
        Buffer.BlockCopy(nonce, 0, sealed_, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, sealed_, NonceSize, TagSize);
        Buffer.BlockCopy(cipher, 0, sealed_, NonceSize + TagSize, cipher.Length);
        return sealed_;
    }

    private static byte[] Open(byte[] key, byte[] sealed_)
    {
        if (sealed_.Length < NonceSize + TagSize)
            throw new CryptographicException("Ciphertext is too short.");

        var nonce = sealed_.AsSpan(0, NonceSize);
        var tag = sealed_.AsSpan(NonceSize, TagSize);
        var cipher = sealed_.AsSpan(NonceSize + TagSize);

        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);   // throws CryptographicException on auth failure
        return plain;
    }
}
