using System;
using Graticula.Host;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// A source taken out of service is refused, and the refusal ends by itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-059](../../docs/adr/ADR-059-quiescing-a-data-source.md).</b> Quiesce is the fifth of
/// [ADR-007](../../docs/adr/ADR-007-service-runtime.md) §4.8's five bullets and the one that was
/// never built: a held connection blocks a DBA's <c>ALTER TABLE</c>, and on PostgreSQL the
/// waiting DDL queues every request behind it.
/// </para>
/// <para>
/// <b>What is worth testing here is the clock, not the dictionary.</b> Every other refusal in
/// this server ends on its own; this is the only one a person starts, so the deadline is the
/// safety and everything below is about it. A test that waited fifteen minutes would not be run,
/// which is why the register takes its time from a function.
/// </para>
/// </remarks>
public sealed class SourceQuiesceTests
{
    private const string Source = "Host=one;Database=gis";

    /// <summary>A clock a test can move.</summary>
    private sealed class Clock
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 8, 14, 0, 0, TimeSpan.Zero);

        public DateTimeOffset Read() => Now;
    }

    [Fact]
    public void A_source_is_held_until_its_window_ends()
    {
        Clock clock = new();
        SourceQuiesce quiesce = new(clock.Read);

        quiesce.Hold(Source, "erdinc", TimeSpan.FromMinutes(5), "adding a column");

        Assert.NotNull(quiesce.Holding(Source));

        clock.Now = clock.Now.AddMinutes(4);
        Assert.NotNull(quiesce.Holding(Source));

        clock.Now = clock.Now.AddMinutes(2);

        Assert.Null(
            quiesce.Holding(Source));
    }

    /// <summary>
    /// The window ending removes the entry rather than leaving it to be reported.
    /// </summary>
    /// <remarks>
    /// <b>A listing that showed a quiesce which had already ended would be the status page
    /// reporting an outage that is over</b>, which is a class of mistake this repository has had
    /// to correct more than once.
    /// </remarks>
    [Fact]
    public void A_lapsed_hold_disappears_from_the_listing()
    {
        Clock clock = new();
        SourceQuiesce quiesce = new(clock.Read);

        quiesce.Hold(Source, "erdinc", TimeSpan.FromMinutes(5), null);
        Assert.Single(quiesce.Current());

        clock.Now = clock.Now.AddMinutes(6);

        Assert.Empty(quiesce.Current());
        Assert.Equal(0, quiesce.Count);
    }

    /// <summary>
    /// An operator can put the source back before the deadline.
    /// </summary>
    [Fact]
    public void Resuming_ends_a_hold_early()
    {
        SourceQuiesce quiesce = new();

        quiesce.Hold(Source, "erdinc", TimeSpan.FromMinutes(15), null);

        Assert.True(quiesce.Resume(Source));
        Assert.Null(quiesce.Holding(Source));

        // <b>And resuming twice is not an error.</b> A window that ended by itself leaves nothing
        // to resume, and an operator pressing it after lunch has done nothing wrong.
        Assert.False(quiesce.Resume(Source));
    }

    /// <summary>
    /// A window longer than the ceiling is clamped rather than refused.
    /// </summary>
    /// <remarks>
    /// <b>The deadline exists so a forgotten quiesce ends</b>, so a caller able to ask for a day
    /// would have removed the safety by using the feature. Refusing instead of clamping would
    /// teach an operator to quiesce twice in a row, which is the same outcome with an extra step.
    /// </remarks>
    [Fact]
    public void A_window_longer_than_the_ceiling_is_clamped()
    {
        Clock clock = new();
        SourceQuiesce quiesce = new(clock.Read);

        SourceQuiesce.Held held =
            quiesce.Hold(Source, "erdinc", TimeSpan.FromDays(1), null);

        Assert.Equal(clock.Now + SourceQuiesce.LongestWindow, held.Until);
    }

    /// <summary>
    /// Asking for nothing, or for a negative window, gets the default rather than a hold of zero.
    /// </summary>
    /// <remarks>
    /// <b>A zero-length hold is the worst of both.</b> It closes the pool — the DBA's connections
    /// go — and then refuses nothing, so the next request rebuilds the pool immediately and the
    /// DDL is blocked again by a server that reported success.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-30)]
    public void An_absent_or_impossible_window_becomes_the_default(int? seconds)
    {
        Clock clock = new();
        SourceQuiesce quiesce = new(clock.Read);

        SourceQuiesce.Held held = quiesce.Hold(
            Source,
            "erdinc",
            seconds is { } s ? TimeSpan.FromSeconds(s) : null,
            null);

        Assert.Equal(clock.Now + SourceQuiesce.DefaultWindow, held.Until);
    }

    /// <summary>
    /// The refusal names who, why and until when.
    /// </summary>
    /// <remarks>
    /// <b>ADR-059 §5e.</b> A planned, deliberate, self-ending unavailability that reads like a
    /// network fault sends whoever is on call to the wrong place at the one moment somebody
    /// already knows the answer — [D-150](../../docs/architecture-debt.md) is this server's own
    /// instance of that mistake.
    /// </remarks>
    [Fact]
    public void The_refusal_says_who_why_and_until_when()
    {
        Clock clock = new();
        SourceQuiesce quiesce = new(clock.Read);

        SourceQuiesce.Held held =
            quiesce.Hold(Source, "erdinc", TimeSpan.FromMinutes(15), "adding a column");

        string says = SourceQuiesce.Says(held);

        Assert.Contains("erdinc", says, StringComparison.Ordinal);
        Assert.Contains("adding a column", says, StringComparison.Ordinal);
        Assert.Contains("14:00", says, StringComparison.Ordinal);
        Assert.Contains("14:15", says, StringComparison.Ordinal);

        // <b>And it says the database is fine</b>, which is the sentence that stops a page.
        Assert.Contains("Nothing is wrong with the database", says, StringComparison.Ordinal);
    }

    /// <summary>
    /// Quiescing one source leaves the others answering.
    /// </summary>
    /// <remarks>
    /// <b>ADR-059 §5d: per data source, which is the unit the lock lives on.</b> A hundred
    /// services can share one registered database and the DBA is altering a table in that one;
    /// a register that took them all out would make the feature unusable on a busy server.
    /// </remarks>
    [Fact]
    public void Holding_one_source_leaves_another_answering()
    {
        SourceQuiesce quiesce = new();

        quiesce.Hold(Source, "erdinc", TimeSpan.FromMinutes(15), null);

        Assert.NotNull(quiesce.Holding(Source));
        Assert.Null(quiesce.Holding("Host=two;Database=gis"));
    }
}
