using System;
using System.Security.Cryptography;
using System.Text;

namespace Graticula.Platform.Secrets;

/// <summary>
/// Encrypts and decrypts secrets held in the platform store.
/// </summary>
/// <remarks>
/// <para>
/// ADR-002 §4.7: secrets are encrypted at rest with a key supplied externally at
/// startup, so a database dump does not hand over every registered credential.
/// </para>
/// <para>
/// <b>The key version travels with the ciphertext</b>, and that is the whole
/// reason this is not just a call to <c>AesGcm</c>. Independent review finding
/// <b>O7</b> observed that rotation has no design, and that restoring a backup
/// taken before a rotation would make every credential undecryptable with the
/// symptom appearing as an authentication failure at each provider individually —
/// a diagnosis that appears in no surface. Recording which key sealed a row is
/// what makes that diagnosable, and it cannot be added later to rows already
/// written.
/// </para>
/// <para>
/// <b>Rotation itself is still not designed</b>, and this does not pretend
/// otherwise. What it does is make the eventual design possible and the failure
/// legible in the meantime.
/// </para>
/// <para>
/// AES-GCM: authenticated, so tampering is detected rather than producing a
/// plausible-looking connection string. The nonce is random per encryption and
/// stored alongside — GCM's failure mode is catastrophic on nonce reuse, so it is
/// never derived from anything reusable.
/// </para>
/// <para>
/// <b>This file was absent from the repository until 2026-08-18, and nothing
/// said so.</b> <c>.gitignore</c> carried <c>secrets/</c> — a rule meant for a
/// deployment's own credential files — and the pattern matched this source
/// directory, case-insensitively, on the platform this was written on. So the type
/// that seals every registered data source credential was never committed, and a
/// clone of this repository could not build. Found by deleting the working copy
/// and recovering the file from a build output; the ignore rule is now anchored,
/// and D-62 records it. A pattern that hides source is worse than one that misses
/// a secret, because the second fails loudly at review and the first fails when
/// somebody else clones.
/// </para>
/// </remarks>
public sealed class SecretProtector
{
    /// <summary>AES-GCM's nonce, in bytes. Twelve is the size GCM is defined for.</summary>
    private const int NonceSize = 12;

    /// <summary>The authentication tag, in bytes. Sixteen is the full-strength tag.</summary>
    private const int TagSize = 16;

    /// <summary>The key, in bytes. Thirty-two is AES-256.</summary>
    private const int KeySize = 32;

    private readonly byte[] _key;

    /// <summary>Creates a protector.</summary>
    /// <param name="keyVersion">
    /// Which key this is. Recorded with every ciphertext so a mismatch after a
    /// restore says so instead of failing obscurely.
    /// </param>
    /// <param name="key">32 bytes.</param>
    public SecretProtector(int keyVersion, ReadOnlySpan<byte> key)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(keyVersion, 1);

        if (key.Length != KeySize)
        {
            throw new ArgumentException(
                $"The secret key must be {KeySize} bytes (AES-256); got {key.Length}.",
                nameof(key));
        }

        KeyVersion = keyVersion;
        _key = key.ToArray();
    }

    /// <summary>Which key this protector holds.</summary>
    public int KeyVersion { get; }

    /// <summary>Generates a key. For first-run setup and for tests.</summary>
    public static byte[] GenerateKey() => RandomNumberGenerator.GetBytes(KeySize);

    /// <summary>Encrypts a secret.</summary>
    /// <returns>Nonce, tag and ciphertext, concatenated.</returns>
    public byte[] Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        byte[] utf8 = Encoding.UTF8.GetBytes(plaintext);
        byte[] sealed_ = new byte[NonceSize + TagSize + utf8.Length];

        Span<byte> nonce = sealed_.AsSpan(0, NonceSize);
        Span<byte> tag = sealed_.AsSpan(NonceSize, TagSize);
        Span<byte> ciphertext = sealed_.AsSpan(NonceSize + TagSize);

        RandomNumberGenerator.Fill(nonce);

        using AesGcm aes = new(_key, TagSize);
        aes.Encrypt(nonce, utf8, ciphertext, tag);

        CryptographicOperations.ZeroMemory(utf8);

        return sealed_;
    }

    /// <summary>Decrypts a secret.</summary>
    /// <param name="protectedSecret">What <see cref="Protect"/> produced.</param>
    /// <param name="keyVersion">
    /// The version recorded alongside the ciphertext. Checked so that a key
    /// mismatch reports itself rather than surfacing as authentication failures
    /// against every provider at once.
    /// </param>
    /// <exception cref="SecretProtectionException">
    /// The version does not match, the buffer is malformed, or authentication
    /// failed.
    /// </exception>
    public string Unprotect(ReadOnlySpan<byte> protectedSecret, int keyVersion)
    {
        if (keyVersion != KeyVersion)
        {
            throw new SecretProtectionException(
                $"This secret was sealed with key version {keyVersion} and the server holds "
                + $"version {KeyVersion}. This usually means a backup taken before a key rotation "
                + "has been restored, or the wrong key was supplied at startup. No amount of "
                + "retrying will help, and every registered credential will fail the same way.");
        }

        if (protectedSecret.Length < NonceSize + TagSize)
        {
            throw new SecretProtectionException(
                $"A sealed secret is at least {NonceSize + TagSize} bytes; this one is "
                + $"{protectedSecret.Length}. The column has been truncated or overwritten.");
        }

        ReadOnlySpan<byte> nonce = protectedSecret[..NonceSize];
        ReadOnlySpan<byte> tag = protectedSecret.Slice(NonceSize, TagSize);
        ReadOnlySpan<byte> ciphertext = protectedSecret[(NonceSize + TagSize)..];

        byte[] utf8 = new byte[ciphertext.Length];

        try
        {
            using AesGcm aes = new(_key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, utf8);
        }
        catch (AuthenticationTagMismatchException e)
        {
            throw new SecretProtectionException(
                "The sealed secret failed authentication. Either the ciphertext was modified, or "
                + "the key is wrong in a way the version check did not catch.", e);
        }

        try
        {
            return Encoding.UTF8.GetString(utf8);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(utf8);
        }
    }
}
