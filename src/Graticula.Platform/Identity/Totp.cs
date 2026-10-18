using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Graticula.Platform.Identity;

/// <summary>
/// Time-based one-time passwords, RFC 6238 over RFC 4226 — ADR-201: HMAC-SHA1, thirty-second steps, six digits, the
/// parameters every authenticator app reads from an <c>otpauth://</c> address without being told.
/// </summary>
/// <remarks>
/// <b>Written here rather than adopted</b>: it is forty lines over the framework's HMAC, and a dependency for it would be
/// a Tier 2 library behind a port for less code than the port.
/// </remarks>
public static class Totp
{
    /// <summary>Seconds in a step.</summary>
    public const int Period = 30;

    /// <summary>Digits in a code.</summary>
    public const int Digits = 6;

    /// <summary>A new secret: 160 bits, RFC 4226's recommended length.</summary>
    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(20);

    /// <summary>The step a moment falls in.</summary>
    public static long StepOf(DateTimeOffset at) => at.ToUnixTimeSeconds() / Period;

    /// <summary>The code for one step.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "RFC 6238's default, the one every authenticator app assumes; HMAC-SHA1 is not weakened by SHA-1's collisions.")]
    public static string CodeAt(byte[] secret, long step)
    {
        ArgumentNullException.ThrowIfNull(secret);

        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        byte[] hash = HMACSHA1.HashData(secret, counter);

        int offset = hash[^1] & 0x0F;
        int binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];

        return (binary % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The step a code matches, one step either side of now to allow for a clock that drifts, and only after the last
    /// step used — a code is spent once; null when it matches none.
    /// </summary>
    public static long? Match(byte[] secret, string code, DateTimeOffset now, long lastUsed)
    {
        ArgumentNullException.ThrowIfNull(secret);

        if (code is not { Length: Digits } || !long.TryParse(code, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            return null;
        }

        long current = StepOf(now);

        for (long step = current - 1; step <= current + 1; step++)
        {
            if (step > lastUsed
                && CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(CodeAt(secret, step)), Encoding.ASCII.GetBytes(code)))
            {
                return step;
            }
        }

        return null;
    }

    /// <summary>The address an authenticator app reads from a QR code or a pasted link.</summary>
    public static string Address(string issuer, string account, byte[] secret) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}"
        + $"?secret={Base32(secret)}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={Period}";

    /// <summary>RFC 4648 base32 without padding, the form an authenticator app is typed.</summary>
    public static string Base32(ReadOnlySpan<byte> data)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        StringBuilder text = new((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;

        foreach (byte b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;

            while (bits >= 5)
            {
                text.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            text.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return text.ToString();
    }
}
