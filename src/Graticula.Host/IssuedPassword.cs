using System;
using System.Security.Cryptography;

namespace Graticula.Host;

/// <summary>
/// A password the server chooses, for an account somebody else will own.
/// </summary>
/// <remarks>
/// <para>
/// <b>Owner rule 2026-08-17:</b> *"kullanıcıya yeni parola veremem. sistem bana yeni bir parola
/// verir. bunu kullanıcı ile paylaşabilirim. ama sistem otomatik olarak o parolayı kirli kabul
/// eder."* — an administrator cannot give a user a new password; the system issues one, the
/// administrator may pass it along, and the system treats it as dirty from the moment it exists.
/// </para>
/// <para>
/// <b>What this replaces, and why the replacement is not merely tidier.</b> The first version had
/// the administrator type both the first password and any reset, and the endpoint's own note
/// admitted the consequence — *"this one is known to whoever typed it here"* — and then did nothing
/// about it. A note describing a hazard is not a control. It also put the administrator's habits
/// on somebody else's account: their idea of *long enough*, their reuse, their pattern across the
/// three accounts they made that morning.
/// </para>
/// <para>
/// <b>Two properties, and both matter.</b> It has to be strong enough that being seen in a chat
/// message for an hour does not matter much, and it has to be **readable aloud and typable**,
/// because it will be — down a corridor, in a phone call, pasted into a message. Those pull in
/// opposite directions, and the resolution is a large alphabet with the ambiguous characters
/// removed rather than a short string with punctuation in it.
/// </para>
/// </remarks>
internal static class IssuedPassword
{
    /// <summary>
    /// The alphabet, with everything a reader could confuse taken out.
    /// </summary>
    /// <remarks>
    /// <b>Crockford's exclusions, for Crockford's reason.</b> <c>I</c>, <c>l</c> and <c>1</c> are
    /// one shape in most fonts, and so are <c>O</c> and <c>0</c>; <c>u</c> is dropped because it
    /// turns typos into words. Lower case throughout, because a password read over the phone
    /// should not need the word *capital* in it. Thirty-one characters, which is where the entropy
    /// arithmetic below starts.
    /// </remarks>
    private const string Alphabet = "abcdefghjkmnpqrstvwxyz23456789";

    /// <summary>How many characters, in groups of four.</summary>
    /// <remarks>
    /// <b>Sixteen characters of a thirty-character alphabet is about 78 bits</b>, which is far past
    /// anything guessable online — the login throttle (ADR-015 §7) makes that argument moot anyway
    /// — and past offline cracking of the Argon2id hash it becomes. The length is chosen for the
    /// **hyphens**: four groups of four is a shape somebody can read out and check they have, which
    /// a run of sixteen is not.
    /// </remarks>
    private const int Characters = 16;

    /// <summary>Issues one.</summary>
    /// <returns>The password, in four hyphenated groups.</returns>
    /// <remarks>
    /// <b><see cref="RandomNumberGenerator.GetString"/>, not a modulo of random bytes.</b> The
    /// obvious hand-rolled version — take a byte, take it modulo the alphabet length — is biased
    /// whenever the length does not divide 256, and thirty does not. The bias is small and the
    /// reason for not writing it is that nobody reviewing this file should have to work out whether
    /// it is small enough.
    /// </remarks>
    public static string Issue()
    {
        string raw = RandomNumberGenerator.GetString(Alphabet, Characters);

        return string.Join(
            '-',
            raw[..4], raw[4..8], raw[8..12], raw[12..]);
    }
}
