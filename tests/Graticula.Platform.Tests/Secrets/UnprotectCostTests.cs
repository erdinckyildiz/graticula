using System;
using System.Diagnostics;
using Graticula.Platform.Secrets;
using Xunit;
using Xunit.Abstractions;

namespace Graticula.Platform.Tests.Secrets;

/// <summary>
/// What unsealing a connection string costs, because a register row was guessing.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-167](../../../docs/architecture-debt.md) split the per-request catalogue read into
/// about 0.9 ms of transport and query and about 1.25 ms of the server's own work, and named
/// three candidates for the second without measuring them: Npgsql materialisation, building a
/// <c>PublishedLayer</c>, and this — <c>PostgresLayerCatalog</c> unseals the data source's
/// connection string on every request.</b> The row says plainly that the candidate somebody
/// would bet on is not evidence. This is the evidence for one of the three.
/// </para>
/// <para>
/// <b>A test rather than a benchmark, and the bar is deliberately loose.</b> It is not
/// measuring how fast AES-GCM is; it is answering *can this account for a millisecond*. A
/// tight threshold on a shared machine is a test that fails on a busy afternoon and teaches
/// nobody anything, so it asserts an order of magnitude and reports the number.
/// </para>
/// </remarks>
public sealed class UnprotectCostTests(ITestOutputHelper output)
{
    /// <summary>How many unseals to time. Enough that a single scheduling hiccup is diluted.</summary>
    private const int Iterations = 20_000;

    /// <summary>
    /// The ceiling this asserts, per unseal. D-167's unexplained slice is 1.25 ms, so 100 µs
    /// is generous by more than ten times and still answers the question.
    /// </summary>
    private const double CeilingMicroseconds = 100;

    [Fact]
    public void Unsealing_a_connection_string_is_microseconds_not_milliseconds()
    {
        // 32 zero bytes: a valid AES-256 key and obviously not a real one.
        SecretProtector protector = new(1, new byte[32]);

        // The shape the catalogue actually unseals — a PostgreSQL connection string with a
        // password in it, which is what makes the length representative.
        const string Secret =
            "Host=some-database.internal;Port=5432;Database=gis;Username=gis;"
            + "Password=a-realistic-length-of-secret;Search Path=hosted,public";

        byte[] sealed_ = protector.Protect(Secret);

        // Warm the JIT and the key schedule; the first call of anything is not the question.
        for (int i = 0; i < 1_000; i++)
        {
            _ = protector.Unprotect(sealed_, 1);
        }

        Stopwatch clock = Stopwatch.StartNew();

        for (int i = 0; i < Iterations; i++)
        {
            _ = protector.Unprotect(sealed_, 1);
        }

        clock.Stop();

        double each = clock.Elapsed.TotalMilliseconds * 1000 / Iterations;

        output.WriteLine(
            $"Unprotect: {each:F2} µs each over {Iterations:N0} iterations "
            + $"({clock.Elapsed.TotalMilliseconds:F0} ms total).");

        Assert.True(
            each < CeilingMicroseconds,
            $"Unsealing took {each:F2} µs each, which is not microseconds any more. If this is "
            + "real rather than a busy machine, D-167's unexplained 1.25 ms has found its "
            + "cause and the row can act rather than measure.");
    }
}
