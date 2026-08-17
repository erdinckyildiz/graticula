using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Graticula.Platform.Identity;
using Konscious.Security.Cryptography;

namespace Graticula.Security.Argon2;

/// <summary>
/// Argon2id, via Konscious.Security.Cryptography.
/// </summary>
/// <remarks>
/// <para>
/// Tier 2 (<c>build-vs-adopt-policy.md</c> §5), behind
/// <see cref="IPasswordHasher"/>. No Konscious type appears in a Tier 1
/// signature, so replacing this — with libsodium, or with whatever .NET ships if
/// it ever ships one — touches this file and nothing else.
/// </para>
/// <para>
/// <b>Cost parameters.</b> Argon2id is tuned by three numbers and there is no
/// universally right set. These follow the OWASP Password Storage Cheat Sheet's
/// second-listed configuration — 19 MiB, 2 iterations, 1 degree of parallelism —
/// which is chosen for servers where memory per concurrent login matters more
/// than raw resistance.
/// </para>
/// <para>
/// <b>The memory figure is the one with a consequence attached.</b> It is
/// allocated per concurrent hash, so 19 MiB × the number of simultaneous logins
/// is real memory, and this is a server where A-037 measured allocation as the
/// binding constraint. Raising it strengthens each password and turns the login
/// endpoint into a memory amplifier: this is precisely why
/// <see cref="LoginThrottle"/> gates on the source address <em>before</em>
/// verification rather than after.
/// </para>
/// <para>
/// <b>Not measured on our hardware.</b> OWASP's numbers are a floor from a
/// document, not a measurement of this server, and the honest calibration is to
/// raise cost until a login takes a target wall time on the deployment's own
/// machine. Because <see cref="PasswordHash.Parameters"/> is stored per
/// credential, doing that later re-hashes each password on its next login rather
/// than invalidating any.
/// </para>
/// </remarks>
public sealed class Argon2idPasswordHasher : IPasswordHasher
{
    /// <summary>The algorithm name written to the store.</summary>
    public const string AlgorithmName = "argon2id";

    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    private readonly Argon2Parameters _current;

    /// <summary>Creates a hasher at the default cost.</summary>
    public Argon2idPasswordHasher()
        : this(new Argon2Parameters(MemoryKib: 19 * 1024, Iterations: 2, Parallelism: 1))
    {
    }

    /// <summary>Creates a hasher at a chosen cost.</summary>
    /// <param name="parameters">The cost to hash new passwords at.</param>
    public Argon2idPasswordHasher(Argon2Parameters parameters)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(parameters.MemoryKib, 8);
        ArgumentOutOfRangeException.ThrowIfLessThan(parameters.Iterations, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(parameters.Parallelism, 1);

        _current = parameters;
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentException">
    /// The password is empty. Refused here with our own message rather than
    /// letting the underlying library throw its own: Konscious rejects an empty
    /// password, and an exception whose text names a third-party type is a
    /// dependency leaking through a port that exists to contain it.
    /// </exception>
    public PasswordHash Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        if (password.Length == 0)
        {
            throw new ArgumentException(
                "An empty password cannot be hashed. Minimum length is an endpoint policy with a "
                + "message that can explain itself; this layer only refuses the degenerate case.",
                nameof(password));
        }

        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] derived = Derive(password, salt, _current);

        // Salt and hash concatenated. The alternative — a PHC-style encoded
        // string carrying both plus the parameters — duplicates what the
        // algorithm and parameters columns already hold, and two copies of the
        // same fact eventually disagree.
        byte[] stored = new byte[SaltBytes + HashBytes];
        salt.CopyTo(stored, 0);
        derived.CopyTo(stored, SaltBytes);

        CryptographicOperations.ZeroMemory(derived);
        return new PasswordHash(AlgorithmName, _current.ToJson(), stored);
    }

    /// <inheritdoc/>
    public bool Verify(string password, PasswordHash stored)
    {
        ArgumentNullException.ThrowIfNull(password);

        // An unreadable credential is a failed login, never an exception. A row
        // written by a future version, or a hash truncated by a bad restore,
        // must not become a 500 that names the algorithm it could not read.
        if (!string.Equals(stored.Algorithm, AlgorithmName, StringComparison.Ordinal)
            || stored.Hash is not { Length: SaltBytes + HashBytes }
            || !Argon2Parameters.TryParse(stored.Parameters, out Argon2Parameters parameters))
        {
            return false;
        }

        // No stored hash was ever produced from an empty password, so nothing
        // can match one. False rather than a throw: the interface contract is
        // that verification answers a question, and every failure to answer it
        // is a failed login.
        if (password.Length == 0)
        {
            return false;
        }

        byte[] salt = stored.Hash[..SaltBytes];
        byte[] derived = Derive(password, salt, parameters);

        try
        {
            return CryptographicOperations.FixedTimeEquals(
                derived, stored.Hash.AsSpan(SaltBytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derived);
        }
    }

    /// <inheritdoc/>
    public bool NeedsRehash(PasswordHash stored) =>
        !string.Equals(stored.Algorithm, AlgorithmName, StringComparison.Ordinal)
        || !Argon2Parameters.TryParse(stored.Parameters, out Argon2Parameters parameters)
        || parameters.IsWeakerThan(_current);

    private static byte[] Derive(string password, byte[] salt, Argon2Parameters parameters)
    {
        using Argon2id argon = new(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = parameters.MemoryKib,
            Iterations = parameters.Iterations,
            DegreeOfParallelism = parameters.Parallelism,
        };

        return argon.GetBytes(HashBytes);
    }
}

/// <summary>Argon2id cost parameters.</summary>
/// <param name="MemoryKib">Memory in kibibytes, allocated per concurrent hash.</param>
/// <param name="Iterations">Passes over that memory.</param>
/// <param name="Parallelism">Lanes.</param>
public readonly record struct Argon2Parameters(int MemoryKib, int Iterations, int Parallelism)
{
    /// <summary>Serialises for the <c>parameters</c> column.</summary>
    public string ToJson() => string.Create(
        CultureInfo.InvariantCulture,
        $$"""{"m":{{MemoryKib}},"t":{{Iterations}},"p":{{Parallelism}}}""");

    /// <summary>Parses what <see cref="ToJson"/> wrote.</summary>
    /// <param name="json">The stored parameters.</param>
    /// <param name="parameters">The parsed value.</param>
    /// <returns>Whether it could be read.</returns>
    public static bool TryParse(string? json, out Argon2Parameters parameters)
    {
        parameters = default;

        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            if (!root.TryGetProperty("m", out JsonElement memory)
                || !root.TryGetProperty("t", out JsonElement iterations)
                || !root.TryGetProperty("p", out JsonElement parallelism))
            {
                return false;
            }

            parameters = new Argon2Parameters(
                memory.GetInt32(), iterations.GetInt32(), parallelism.GetInt32());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            // A property present but not a number. Same answer: unreadable.
            return false;
        }
    }

    /// <summary>
    /// Whether this cost is weaker than another in any dimension.
    /// </summary>
    /// <remarks>
    /// <b>Any</b>, not all. A credential hashed with more memory but fewer
    /// iterations than the current policy is not comparable by a single ordering,
    /// and treating it as strong enough because one number is higher is how a
    /// cost reduction slips through unnoticed. Re-hashing costs one extra hash on
    /// one login; being wrong here costs a weaker password store.
    /// </remarks>
    public bool IsWeakerThan(Argon2Parameters other) =>
        MemoryKib < other.MemoryKib
        || Iterations < other.Iterations
        || Parallelism < other.Parallelism;
}
