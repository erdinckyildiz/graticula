using System;

namespace Graticula.Platform.Identity;

/// <summary>
/// A stored password, with the parameters that produced it.
/// </summary>
/// <param name="Algorithm">
/// The algorithm name as written to <c>local_credential.algorithm</c>.
/// </param>
/// <param name="Parameters">
/// The cost parameters, as JSON, exactly as written to
/// <c>local_credential.parameters</c>.
/// </param>
/// <param name="Hash">The derived bytes, salt included where the format carries it.</param>
/// <remarks>
/// <b>The parameters travel with the hash</b>, per row rather than per server.
/// Argon2id costs are raised over time as hardware improves, and a server-wide
/// setting means the day the cost is raised is the day every existing password
/// stops verifying. Storing them per credential lets a login verify against
/// whatever sealed it and then re-hash at the current cost — see
/// <see cref="IPasswordHasher.NeedsRehash"/>.
/// </remarks>
public readonly record struct PasswordHash(string Algorithm, string Parameters, byte[] Hash);

/// <summary>
/// Hashes and verifies passwords.
/// </summary>
/// <remarks>
/// <para>
/// <b>A Tier 1 port over a Tier 2 implementation</b>
/// (<c>build-vs-adopt-policy.md</c> §4). Password hashing is the one place where
/// writing it ourselves would be indefensible: the algorithm is specified, the
/// implementations are audited, and a subtle error is undetectable by testing —
/// a wrong Argon2id produces hashes that verify against themselves perfectly
/// while being far weaker than intended.
/// </para>
/// <para>
/// The port exists so no library type appears in a Tier 1 signature, which keeps
/// the choice replaceable. That matters more here than usual: the .NET Argon2id
/// options are all small community packages, and the one we picked may not be
/// the one we finish with.
/// </para>
/// </remarks>
public interface IPasswordHasher
{
    /// <summary>Hashes a password at the current cost.</summary>
    /// <param name="password">The plaintext.</param>
    /// <returns>The hash and the parameters that produced it.</returns>
    PasswordHash Hash(string password);

    /// <summary>
    /// Verifies a password against a stored hash.
    /// </summary>
    /// <param name="password">The plaintext offered.</param>
    /// <param name="stored">What the store holds.</param>
    /// <returns>Whether they match.</returns>
    /// <remarks>
    /// Must compare in constant time, and must return <c>false</c> rather than
    /// throw for a hash whose algorithm it does not recognise — an unreadable
    /// credential is a failed login, not a server error, and throwing would turn
    /// a stale row into a 500 that names the algorithm.
    /// </remarks>
    bool Verify(string password, PasswordHash stored);

    /// <summary>
    /// Whether a stored hash was produced at a weaker cost than the current one.
    /// </summary>
    /// <param name="stored">What the store holds.</param>
    /// <returns>Whether it should be re-hashed after a successful verification.</returns>
    bool NeedsRehash(PasswordHash stored);
}
