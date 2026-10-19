using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Secrets;

namespace Graticula.Platform.Identity;

/// <summary>One principal's second factor as stored: the secret sealed, the last step spent, recovery codes hashed.</summary>
/// <param name="SealedSecret">The base32 secret, sealed by the server's key.</param>
/// <param name="KeyVersion">Which key sealed it.</param>
/// <param name="Enabled">Whether it is asked for at sign-in; false between enrolling and confirming.</param>
/// <param name="LastStep">The last time step a code was accepted for, so a code is spent once.</param>
/// <param name="Recovery">The SHA-256 of each unspent recovery code.</param>
public sealed record SecondFactorRecord(
    byte[] SealedSecret, int KeyVersion, bool Enabled, long LastStep, IReadOnlyList<byte[]> Recovery);

/// <summary>Where second factors are kept — ADR-201.</summary>
public interface ISecondFactorStore
{
    /// <summary>A principal's second factor, or null when it has none.</summary>
    Task<SecondFactorRecord?> FindAsync(Guid principal, CancellationToken cancellationToken);

    /// <summary>Starts enrolling: a new secret, not yet asked for; false when one is already enabled.</summary>
    Task<bool> BeginAsync(Guid principal, byte[] sealedSecret, int keyVersion, CancellationToken cancellationToken);

    /// <summary>Turns an enrolment on, with the step its confirming code was for and the recovery codes' hashes.</summary>
    Task<bool> EnableAsync(Guid principal, long timeStep, IReadOnlyList<byte[]> recovery, CancellationToken cancellationToken);

    /// <summary>Records a step as spent; false when it, or a later one, already was — a replay.</summary>
    Task<bool> AdvanceAsync(Guid principal, long timeStep, CancellationToken cancellationToken);

    /// <summary>Spends a recovery code by its hash; false when it is not one of the unspent codes.</summary>
    Task<bool> SpendRecoveryAsync(Guid principal, byte[] hash, CancellationToken cancellationToken);

    /// <summary>Removes the second factor; false when there was none.</summary>
    Task<bool> RemoveAsync(Guid principal, CancellationToken cancellationToken);
}

/// <summary>
/// A second step at sign-in for an account with a local password: a code from an authenticator app, or a recovery
/// code — ADR-201.
/// </summary>
/// <remarks>
/// <para>
/// <b>The secret is sealed by the server's key</b>, as a data source's password is (<see cref="SecretProtector"/>):
/// the platform store's backup alone does not give anybody a code.
/// </para>
/// <para>
/// <b>Ten recovery codes, shown once and kept as hashes</b>, each spent by use. They are the way back for somebody who
/// lost their phone; an administrator removing the factor is the other.
/// </para>
/// </remarks>
public sealed class SecondFactors
{
    /// <summary>How many recovery codes an enrolment gives.</summary>
    public const int RecoveryCodes = 10;

    private readonly ISecondFactorStore _store;
    private readonly SecretProtector _protector;
    private readonly TimeProvider _time;

    /// <summary>Creates the service.</summary>
    public SecondFactors(ISecondFactorStore store, SecretProtector protector, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(time);

        _store = store;
        _protector = protector;
        _time = time;
    }

    /// <summary>Whether signing in as this principal asks for a second step.</summary>
    public async Task<bool> RequiredAsync(Guid principal, CancellationToken cancellationToken) =>
        (await _store.FindAsync(principal, cancellationToken).ConfigureAwait(false))?.Enabled == true;

    /// <summary>Whether a code — six digits from the app, or a recovery code — passes, spending it if so.</summary>
    public async Task<bool> VerifyAsync(Guid principal, string? code, CancellationToken cancellationToken)
    {
        string typed = (code ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal).Trim();

        if (typed.Length == 0
            || await _store.FindAsync(principal, cancellationToken).ConfigureAwait(false) is not { Enabled: true } factor)
        {
            return false;
        }

        if (typed.Length == Totp.Digits)
        {
            return Totp.Match(Secret(factor), typed, _time.GetUtcNow(), factor.LastStep) is { } step
                && await _store.AdvanceAsync(principal, step, cancellationToken).ConfigureAwait(false);
        }

        return await _store.SpendRecoveryAsync(principal, HashOf(typed), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The enrolment already under way, or a new one — for sign-in, where a member the organisation requires a second
    /// factor of sets one up (ADR-201 §6 condition 2). Null when one is already on.
    /// </summary>
    /// <remarks>
    /// <b>The same secret on every attempt until it is confirmed.</b> A fresh one per attempt would turn the code a
    /// member's app shows into a wrong one each time the page is sent again — after a typo, or a reload — and the app
    /// would hold a secret nothing accepts.
    /// </remarks>
    public async Task<(string Secret, string Address)?> PendingOrBeginAsync(
        Principal principal, string issuer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);

        if (await _store.FindAsync(principal.Id, cancellationToken).ConfigureAwait(false) is { } factor)
        {
            if (factor.Enabled)
            {
                return null;
            }

            string text = _protector.Unprotect(factor.SealedSecret, factor.KeyVersion);
            return (text, Totp.Address(issuer, principal.Name, Base32Bytes(text)));
        }

        return await BeginAsync(principal, issuer, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts enrolling: the secret, as text to type and as an <c>otpauth://</c> address; null when a second factor is
    /// already on, which has to be removed first.
    /// </summary>
    public async Task<(string Secret, string Address)?> BeginAsync(
        Principal principal, string issuer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);

        byte[] secret = Totp.NewSecret();
        string text = Totp.Base32(secret);

        if (!await _store.BeginAsync(principal.Id, _protector.Protect(text), _protector.KeyVersion, cancellationToken)
                .ConfigureAwait(false))
        {
            return null;
        }

        return (text, Totp.Address(issuer, principal.Name, secret));
    }

    /// <summary>
    /// Finishes enrolling with a code from the app, which proves the secret reached it; the recovery codes, shown once,
    /// or null when the code is wrong or nothing was begun.
    /// </summary>
    public async Task<IReadOnlyList<string>?> ConfirmAsync(Guid principal, string? code, CancellationToken cancellationToken)
    {
        if (await _store.FindAsync(principal, cancellationToken).ConfigureAwait(false) is not { Enabled: false } factor
            || Totp.Match(Secret(factor), (code ?? string.Empty).Trim(), _time.GetUtcNow(), factor.LastStep) is not { } step)
        {
            return null;
        }

        string[] codes = [.. Enumerable.Range(0, RecoveryCodes).Select(_ => RecoveryCode())];

        return await _store.EnableAsync(principal, step, [.. codes.Select(HashOf)], cancellationToken).ConfigureAwait(false)
            ? codes
            : null;
    }

    /// <summary>Removes a principal's second factor.</summary>
    public Task<bool> RemoveAsync(Guid principal, CancellationToken cancellationToken) =>
        _store.RemoveAsync(principal, cancellationToken);

    private byte[] Secret(SecondFactorRecord factor) =>
        Base32Bytes(_protector.Unprotect(factor.SealedSecret, factor.KeyVersion));

    /// <summary>Ten characters of base32 in two groups of five, lower case, as people copy them.</summary>
    private static string RecoveryCode()
    {
        string text = Totp.Base32(RandomNumberGenerator.GetBytes(7))[..10].ToLowerInvariant();
        return $"{text[..5]}-{text[5..]}";
    }

    private static byte[] HashOf(string code) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(code.Trim().ToLowerInvariant()));

    private static byte[] Base32Bytes(string text)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        List<byte> bytes = [];
        int buffer = 0, bits = 0;

        foreach (char c in text)
        {
            buffer = (buffer << 5) | Alphabet.IndexOf(c, StringComparison.Ordinal);
            bits += 5;

            if (bits >= 8)
            {
                bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        return [.. bytes];
    }
}
