using System;
using Graticula.Host;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// A repeating failure is logged once and counted, and the count is reported.
/// </summary>
/// <remarks>
/// <b>[D-133](../../docs/architecture-debt.md) measured 338 warnings and 1.68 MB over one
/// seventeen-minute outage</b> — every one of them the same stack trace. The live
/// measurement after the change is in that row; these tests are the deterministic half,
/// because a rule about *the twentieth repetition* cannot be checked by stopping a
/// database and hoping.
/// </remarks>
public sealed class RepeatedFailureTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_first_failure_is_logged_in_full()
    {
        RepeatedFailure failures = new();

        Assert.Equal(
            RepeatedFailure.Action.InFull,
            failures.Failed("Failed to connect", Start));

        Assert.Equal(1, failures.Times);
    }

    /// <summary>
    /// Repetitions are silent until the count is due.
    /// </summary>
    /// <remarks>
    /// <b>The whole point, stated as a number.</b> Nineteen repetitions after the first
    /// produce nothing, which is nineteen stack traces an operator does not have to
    /// scroll past to find what else happened during the outage.
    /// </remarks>
    [Fact]
    public void A_repetition_is_silent_until_the_count_is_due()
    {
        RepeatedFailure failures = new();

        failures.Failed("Failed to connect", Start);

        for (int i = 2; i < RepeatedFailure.Every; i++)
        {
            Assert.Equal(
                RepeatedFailure.Action.Nothing,
                failures.Failed("Failed to connect", Start.AddSeconds(i * 2)));
        }

        Assert.Equal(
            RepeatedFailure.Action.Summarise,
            failures.Failed("Failed to connect", Start.AddSeconds(RepeatedFailure.Every * 2)));

        Assert.Equal(RepeatedFailure.Every, failures.Times);
    }

    /// <summary>
    /// A different reason starts again rather than adding to the count.
    /// </summary>
    /// <remarks>
    /// <b>Measured in a real outage before this test existed, which is why it is here.</b>
    /// Stopping and starting PostgreSQL produced four distinct messages in sequence —
    /// *Exception while reading from stream*, *Failed to connect*, *the database system is
    /// starting up*, *not yet accepting connections* — and each of those is a different
    /// fact about the outage's progress. Collapsing them under one counter would have
    /// reported the fortieth repetition of something that had just happened once, and
    /// hidden the two that say the database is on its way back.
    /// </remarks>
    [Fact]
    public void A_different_reason_is_logged_in_full_and_restarts_the_count()
    {
        RepeatedFailure failures = new();

        failures.Failed("Failed to connect", Start);
        failures.Failed("Failed to connect", Start.AddSeconds(2));

        Assert.Equal(
            RepeatedFailure.Action.InFull,
            failures.Failed("the database system is starting up", Start.AddSeconds(4)));

        Assert.Equal(1, failures.Times);
    }

    /// <summary>
    /// Recovery reports how many failures there were and over how long.
    /// </summary>
    /// <remarks>
    /// <b>The line that closes an incident, and the one easiest to leave out.</b> A log
    /// that says a worker started failing and never says it stopped leaves the reader to
    /// infer it from an absence, which is the same problem the summary line solves in the
    /// other direction.
    /// </remarks>
    [Fact]
    public void Recovery_reports_the_count_and_the_span()
    {
        RepeatedFailure failures = new();

        failures.Failed("Failed to connect", Start);
        failures.Failed("Failed to connect", Start.AddSeconds(60));

        int times = failures.Recovered(Start.AddSeconds(120), out TimeSpan over);

        Assert.Equal(2, times);
        Assert.Equal(TimeSpan.FromSeconds(120), over);

        // And it does not report the same failures twice.
        Assert.Equal(0, failures.Recovered(Start.AddSeconds(130), out TimeSpan none));
        Assert.Equal(TimeSpan.Zero, none);
    }

    /// <summary>
    /// A success with nothing before it says nothing.
    /// </summary>
    /// <remarks>
    /// <b>Otherwise every idle poll of a healthy server writes a recovery line</b>, which
    /// is the failure this class exists to prevent, arriving through the door marked
    /// success.
    /// </remarks>
    [Fact]
    public void A_success_after_no_failure_reports_nothing()
    {
        RepeatedFailure failures = new();

        Assert.Equal(0, failures.Recovered(Start, out TimeSpan over));
        Assert.Equal(TimeSpan.Zero, over);
    }
}
