using System;
using System.Text;
using GisServer.Platform.Identity;
using Xunit;

namespace GisServer.Security.Argon2.Tests;

/// <summary>
/// The Argon2id adapter.
/// </summary>
/// <remarks>
/// <para>
/// <b>These tests cannot tell you the algorithm is correct.</b> A wrong Argon2id
/// verifies against itself perfectly — that is precisely why we adopted an
/// implementation instead of writing one. What is testable, and what is tested
/// here, is the wrapper: that the parameters round-trip, that an unreadable
/// credential is a failed login rather than an exception, and that a weaker cost
/// is detected.
/// </para>
/// <para>
/// The cost is lowered in most tests. At the shipped cost each hash allocates
/// 19 MiB and takes tens of milliseconds, which would make this file the slowest
/// thing in the suite for no additional coverage.
/// </para>
/// </remarks>
public sealed class Argon2idPasswordHasherTests
{
    private static readonly Argon2Parameters Cheap = new(MemoryKib: 64, Iterations: 1, Parallelism: 1);

    private static Argon2idPasswordHasher Hasher(Argon2Parameters? parameters = null) =>
        new(parameters ?? Cheap);

    [Fact]
    public void A_password_verifies_against_its_own_hash()
    {
        Argon2idPasswordHasher hasher = Hasher();
        PasswordHash stored = hasher.Hash("correct horse battery staple");

        Assert.True(hasher.Verify("correct horse battery staple", stored));
    }

    [Fact]
    public void A_different_password_does_not_verify()
    {
        Argon2idPasswordHasher hasher = Hasher();
        PasswordHash stored = hasher.Hash("correct horse battery staple");

        Assert.False(hasher.Verify("correct horse battery stapl", stored));
    }

    [Fact]
    public void The_same_password_hashes_differently_every_time()
    {
        // A random salt per credential. Without it, two users with the same
        // password have the same stored bytes, which turns one cracked password
        // into every account that shares it and makes the table sortable by
        // popularity.
        Argon2idPasswordHasher hasher = Hasher();

        Assert.NotEqual(
            hasher.Hash("same password").Hash,
            hasher.Hash("same password").Hash);
    }

    [Fact]
    public void The_parameters_are_stored_and_round_trip()
    {
        PasswordHash stored = Hasher(new Argon2Parameters(128, 3, 2)).Hash("whatever");

        Assert.True(Argon2Parameters.TryParse(stored.Parameters, out Argon2Parameters parsed));
        Assert.Equal(new Argon2Parameters(128, 3, 2), parsed);
    }

    [Fact]
    public void A_hash_verifies_against_the_parameters_it_was_made_with_not_the_current_ones()
    {
        // The whole point of storing parameters per credential. If this fails,
        // raising the cost invalidates every existing password at once.
        PasswordHash old = Hasher(new Argon2Parameters(64, 1, 1)).Hash("a long enough password");
        Argon2idPasswordHasher raised = Hasher(new Argon2Parameters(128, 2, 1));

        Assert.True(raised.Verify("a long enough password", old));
    }

    [Fact]
    public void A_hash_made_at_a_lower_cost_needs_rehashing()
    {
        PasswordHash old = Hasher(new Argon2Parameters(64, 1, 1)).Hash("a long enough password");

        Assert.True(Hasher(new Argon2Parameters(128, 1, 1)).NeedsRehash(old));
    }

    [Fact]
    public void A_hash_at_the_current_cost_does_not_need_rehashing()
    {
        Argon2idPasswordHasher hasher = Hasher();

        Assert.False(hasher.NeedsRehash(hasher.Hash("a long enough password")));
    }

    [Fact]
    public void A_cost_that_is_higher_in_one_dimension_and_lower_in_another_still_needs_rehashing()
    {
        // More memory, fewer iterations. There is no single ordering here, and
        // treating "one number went up" as good enough is how a cost reduction
        // slips through. Rehashing costs one extra hash on one login.
        PasswordHash mixed = Hasher(new Argon2Parameters(256, 1, 1)).Hash("a long enough password");

        Assert.True(Hasher(new Argon2Parameters(128, 2, 1)).NeedsRehash(mixed));
    }

    [Theory]
    [InlineData("pbkdf2", """{"m":64,"t":1,"p":1}""")]
    [InlineData("argon2id", "not json at all")]
    [InlineData("argon2id", """{"m":"lots"}""")]
    [InlineData("argon2id", """{"t":1,"p":1}""")]
    public void An_unreadable_credential_is_a_failed_login_never_an_exception(
        string algorithm, string parameters)
    {
        // A row written by a future version, or damaged by a bad restore, must
        // not become a 500 that names the algorithm it could not read.
        Argon2idPasswordHasher hasher = Hasher();
        PasswordHash broken = new(algorithm, parameters, new byte[48]);

        Assert.False(hasher.Verify("anything", broken));
        Assert.True(hasher.NeedsRehash(broken));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(47)]
    [InlineData(49)]
    public void A_hash_of_the_wrong_length_is_a_failed_login(int length)
    {
        // 16 bytes of salt plus 32 of hash. Anything else is truncation, and
        // slicing it would either throw or compare against the wrong bytes.
        Argon2idPasswordHasher hasher = Hasher();

        Assert.False(hasher.Verify("anything", new PasswordHash("argon2id", Cheap.ToJson(), new byte[length])));
    }

    [Fact]
    public void An_empty_password_is_refused_by_us_and_not_by_the_library()
    {
        // Konscious throws its own ArgumentException for an empty password. That
        // exception naming a third-party type would be the dependency leaking
        // through the port that exists to contain it, so the refusal is ours.
        Argon2idPasswordHasher hasher = Hasher();

        ArgumentException thrown =
            Assert.Throws<ArgumentException>(() => hasher.Hash(string.Empty));

        Assert.Contains("empty password", thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Argon2 needs", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Verifying_an_empty_password_is_false_rather_than_an_exception()
    {
        // Nothing was ever hashed from an empty password, so nothing can match
        // one. Verification must answer the question, and every failure to
        // answer it is a failed login rather than a 500.
        Argon2idPasswordHasher hasher = Hasher();

        Assert.False(hasher.Verify(string.Empty, hasher.Hash("a long enough password")));
    }

    [Fact]
    public void A_password_is_treated_as_utf8_so_non_ascii_survives()
    {
        Argon2idPasswordHasher hasher = Hasher();
        const string Password = "パスワード-şifre-пароль";

        Assert.True(hasher.Verify(Password, hasher.Hash(Password)));
    }

    [Fact]
    public void The_salt_is_the_first_sixteen_bytes_and_differs_from_the_hash()
    {
        Argon2idPasswordHasher hasher = Hasher();
        PasswordHash stored = hasher.Hash("a long enough password");

        Assert.Equal(48, stored.Hash.Length);
        Assert.NotEqual(stored.Hash[..16], stored.Hash[16..32]);
    }

    [Fact]
    public void The_algorithm_name_written_is_the_one_verified_against()
    {
        // These are two constants in different places in a real deployment: the
        // string in the database and the string in the code. If they ever drift,
        // every password stops verifying at once.
        Argon2idPasswordHasher hasher = Hasher();

        Assert.Equal(Argon2idPasswordHasher.AlgorithmName, hasher.Hash("x").Algorithm);
    }

    [Fact]
    public void The_default_cost_is_the_one_the_class_documents()
    {
        // OWASP's second configuration: 19 MiB, 2 iterations, 1 lane. Pinned so
        // that lowering it is a visible change to a test rather than a quiet
        // edit to a constructor.
        PasswordHash stored = new Argon2idPasswordHasher().Hash("a long enough password");

        Assert.True(Argon2Parameters.TryParse(stored.Parameters, out Argon2Parameters parameters));
        Assert.Equal(new Argon2Parameters(19 * 1024, 2, 1), parameters);
    }

    [Fact]
    public void A_hash_is_not_the_password_in_any_recoverable_form()
    {
        // Crude, and worth having: it catches the catastrophic wiring mistake
        // where the "hash" is the input with a salt stapled to it.
        const string Password = "recognisable-plaintext-marker";
        PasswordHash stored = Hasher().Hash(Password);

        // Latin1 so every byte maps to a character and nothing is lost to
        // invalid-UTF8 replacement, which could hide the very thing being
        // looked for.
        Assert.DoesNotContain(Password, Encoding.Latin1.GetString(stored.Hash), StringComparison.Ordinal);
    }
}
