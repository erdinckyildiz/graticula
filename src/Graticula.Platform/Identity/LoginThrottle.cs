using System;

namespace Graticula.Platform.Identity;

/// <summary>
/// How many recent failures an account and an address have accumulated.
/// </summary>
/// <param name="ForAccount">Recent failed attempts against the name offered.</param>
/// <param name="ForAddress">Recent failed attempts from the source address.</param>
public readonly record struct FailureCounts(int ForAccount, int ForAddress);

/// <summary>
/// The rate limits on login, and the reason they are shaped this way.
/// </summary>
/// <remarks>
/// <para>
/// ADR-015 §5 requires rate limiting <em>per account and per source address</em>,
/// and states the reason for the second: per-account alone lets an attacker lock
/// out every user they can name, turning a brute-force defence into a
/// denial-of-service tool. Condition 3 requires that inversion to be tested.
/// </para>
/// <para>
/// <b>Adding a second limit does not by itself fix the inversion</b> — it just
/// adds a limit. What fixes it is where each limit sits relative to verifying
/// the password:
/// </para>
/// <list type="number">
/// <item>
/// <description>
/// <b>The address limit is a gate before verification.</b> It has to be, because
/// Argon2id is deliberately expensive: without it, an attacker gets us to burn
/// tens of milliseconds of CPU per guess and the login endpoint becomes a
/// resource-exhaustion lever. This limit blocks work.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>The account limit is consulted only after verification fails.</b> A
/// request carrying the <em>correct</em> password is honoured no matter how many
/// failures the account has accumulated. That is what removes the lockout
/// weapon: an attacker can spend an account's failure budget all day and the
/// person who knows the password still gets in. Every one of the attacker's own
/// guesses is wrong, so every one of them is throttled.
/// </description>
/// </item>
/// </list>
/// <para>
/// <b>What this deliberately does not do:</b> lock an account. Nothing here ever
/// makes a correct password stop working. Disabling an account is an
/// administrator's action with a record attached, not something an anonymous
/// attacker can cause.
/// </para>
/// <para>
/// <b>Both limits are still evadable</b> and pretending otherwise would be
/// worse than saying so: the address limit falls to a botnet, and the account
/// limit slows a targeted guess rather than stopping it. They buy time and make
/// the attempt visible. Password strength and disabling accounts are what
/// actually stop it.
/// </para>
/// </remarks>
public sealed class LoginThrottle
{
    /// <summary>The default policy.</summary>
    /// <remarks>
    /// <b>These numbers are chosen, not measured.</b> Nothing has been observed
    /// about how often a legitimate user mistypes a password against this
    /// server, because nobody has used it yet. They are here so the policy is in
    /// one place with a name, rather than as three literals in a handler.
    /// </remarks>
    public static readonly LoginThrottle Default = new(
        window: TimeSpan.FromMinutes(15), perAccount: 10, perAddress: 50);

    /// <summary>Creates a policy.</summary>
    /// <param name="window">How far back failures are counted.</param>
    /// <param name="perAccount">Failures against one name before it is throttled.</param>
    /// <param name="perAddress">Failures from one address before it is refused.</param>
    public LoginThrottle(TimeSpan window, int perAccount, int perAddress)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(perAccount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(perAddress, 1);

        // The address budget must exceed the account budget, or the address
        // limit fires first for a single user fumbling one password and the
        // account limit is unreachable — a policy that reads as two limits and
        // behaves as one.
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(perAddress, perAccount);

        Window = window;
        PerAccount = perAccount;
        PerAddress = perAddress;
    }

    /// <summary>How far back failures are counted.</summary>
    public TimeSpan Window { get; }

    /// <summary>Failures against one name before it is throttled.</summary>
    public int PerAccount { get; }

    /// <summary>Failures from one address before it is refused.</summary>
    public int PerAddress { get; }

    /// <summary>
    /// Whether to refuse before spending the cost of verifying a password.
    /// </summary>
    /// <remarks>
    /// Address only. Checking the account here is what would create the lockout
    /// weapon, and the whole shape of this class is arranged to avoid it.
    /// </remarks>
    public bool RefuseBeforeVerifying(FailureCounts counts) => counts.ForAddress >= PerAddress;

    /// <summary>
    /// Whether a <em>failed</em> attempt should be reported as throttled.
    /// </summary>
    /// <remarks>
    /// Only ever reached when the password was wrong. A caller that consults
    /// this before verifying has reintroduced the lockout weapon.
    /// </remarks>
    public bool ThrottleAfterFailure(FailureCounts counts) => counts.ForAccount >= PerAccount;
}
