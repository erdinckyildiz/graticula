using System;
using System.Diagnostics;
using System.Linq;
using Graticula.Platform.Identity;
using Graticula.Security.Argon2;
using Xunit;

namespace Graticula.Security.Argon2.Tests;

/// <summary>
/// What the decoy work for an unknown account name actually costs.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-13](../../docs/architecture-debt.md) is a timing gap, and the repair is asserted by
/// counting operations in <c>LoginServiceTests</c> — deliberately, because a count does not
/// flake.</b> This file is the other half: the reason the count is the right thing to count.
/// The old decoy hashed the submitted password and verified against what it had just made, and
/// the claim that this cost about twice a verification is a claim about Argon2 rather than about
/// our code. Claims about Argon2 get measured.
/// </para>
/// <para>
/// <b>Ratios, not milliseconds.</b> An absolute threshold would be a statement about the machine
/// the suite happens to run on. The ratios are structural: one hash plus one verification against
/// one verification, at the same parameters, is two units against one.
/// </para>
/// <para>
/// <b>Middling parameters, not the shipped ones and not this suite's cheap ones.</b> At 64 KiB
/// and one iteration a hash is fast enough that scheduler noise dominates the ratio; at the
/// shipped 19 MiB this file would be the slowest thing in the suite. Four mebibytes and two
/// iterations puts each operation in the low milliseconds, where the ratio is legible and the
/// whole test is under a second.
/// </para>
/// </remarks>
public sealed class DecoyVerificationCostTests
{
    private static readonly Argon2Parameters Middling =
        new(MemoryKib: 4096, Iterations: 2, Parallelism: 1);

    private const int Samples = 15;

    /// <summary>The fastest each of two operations managed, sampled alternately.</summary>
    /// <remarks>
    /// <para>
    /// <b>Alternately, because the first version measured them in sequence and was wrong by a
    /// factor of two.</b> Each hash allocates four mebibytes, so the first batch measured pays
    /// for the heap growing to hold them and the second does not: verifying a real credential
    /// came out at 22.5 ms and verifying the decoy — the same operation on the same
    /// parameters — at 10.9 ms, purely because it ran second.
    /// </para>
    /// <para>
    /// <b>The fastest rather than the median, because the median flaked.</b> This file used
    /// medians and failed once inside a full suite run: 24.9 ms against 18.4 ms, a ratio of 1.35
    /// where it wanted 1.6, on a machine that was also running two overlay workers. A median
    /// carries whatever else the machine was doing; the fastest sample of each is the one least
    /// disturbed by it, and the claim under test is about the work rather than about the load.
    /// </para>
    /// <para>
    /// <b>It is still a comparison and never an absolute.</b> Both halves are sampled in the same
    /// loop on the same machine, so a slow machine moves both.
    /// </para>
    /// </remarks>
    private static (double First, double Second) FastestOf(Action first, Action second)
    {
        // Untimed passes, so no sample carries the JIT or the first allocation.
        first();
        second();

        double[] onto = new double[Samples];
        double[] other = new double[Samples];

        for (int i = 0; i < Samples; i++)
        {
            long start = Stopwatch.GetTimestamp();
            first();
            onto[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            start = Stopwatch.GetTimestamp();
            second();
            other[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        Array.Sort(onto);
        Array.Sort(other);

        return (onto[0], other[0]);
    }

    /// <summary>
    /// Verifying against a fixed decoy costs what verifying a real credential costs.
    /// </summary>
    /// <remarks>
    /// <b>This is the property the repair claims</b>, and the one an account-enumeration probe
    /// would be looking for: an unknown name and a known name at the current parameters do the
    /// same work. The bound is loose because the measurement is of a shared machine, and it is
    /// still far tighter than the factor the old decoy carried.
    /// </remarks>
    [Fact]
    public void A_fixed_decoy_verifies_in_the_time_a_real_credential_does()
    {
        Argon2idPasswordHasher hasher = new(Middling);

        PasswordHash stored = hasher.Hash("correct horse battery staple");
        PasswordHash decoy = hasher.Hash(Guid.NewGuid().ToString("N"));

        (double real, double against) = FastestOf(
            () => hasher.Verify("wrong password", stored),
            () => hasher.Verify("wrong password", decoy));

        Assert.True(
            against < real * 1.4 && real < against * 1.4,
            $"verifying against the decoy took {against:F1} ms and verifying a real credential "
            + $"{real:F1} ms. These are the two halves of an unknown name and a known one, and "
            + "the whole of D-13 is that they should not be distinguishable.");
    }

    /// <summary>
    /// The decoy that was there before cost about twice as much.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The old work is measured rather than described, because the row described it as
    /// *narrowed, not closed* and that was the wrong shape.</b> `SpendComparableTime` hashed the
    /// submitted password and then verified it against itself: one hash and one verification
    /// where the real path does one verification. The gap was not a subtle distributional
    /// difference needing many samples — an unknown name took about twice as long as a known
    /// one, in the opposite direction from the leak the method was written to prevent.
    /// </para>
    /// <para>
    /// <b>Kept as a test rather than a note</b> so the reasoning stays falsifiable: if a future
    /// hasher makes verification cost what hashing costs plus a constant, this fails and the
    /// argument above needs rewriting.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_old_decoy_cost_about_twice_a_verification()
    {
        Argon2idPasswordHasher hasher = new(Middling);

        PasswordHash stored = hasher.Hash("correct horse battery staple");

        (double real, double old) = FastestOf(
            () => hasher.Verify("wrong password", stored),
            () => hasher.Verify("wrong password", hasher.Hash("wrong password")));

        Assert.True(
            old > real * 1.6,
            $"the old decoy took {old:F1} ms against {real:F1} ms for a real verification. This "
            + "test exists to keep D-13's account of itself honest: if hashing has become free, "
            + "the row's claim that the old work was measurably different is no longer true.");
    }
}
