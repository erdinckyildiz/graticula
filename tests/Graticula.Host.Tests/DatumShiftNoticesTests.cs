using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Geometries;
using Graticula.Host;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// The operator-facing datum caution: what it records, what it refuses to record, and how
/// often it says so.
/// </summary>
/// <remarks>
/// <para>
/// <b>[Q-141](../../docs/open-questions.md), owner decision 2026-08-25 — *"Operatöre söyle —
/// günlük ve /admin"*.</b> The value of this thing is entirely in its restraint. A caution
/// that appears on every request is filtered out, a caution that appears for
/// 4326&#8202;→&#8202;3857 is noise, and a caution that never appears is
/// [D-32](../../docs/architecture-debt.md) unchanged. So most of these tests are about
/// silence.
/// </para>
/// <para>
/// <b>Measured end to end on 2026-08-25 as well as here.</b> A FeatureServer query with
/// <c>outSR=4326</c> against a layer stored in EPSG:5254 logged once and appeared under
/// <c>datumShifts</c> on <c>/admin/health</c>; six queries produced one line; a layer stored
/// in EPSG:3857 served as EPSG:4326 produced none; and a vector tile request produced the
/// 5254&#8202;→&#8202;3857 line with no query involved. These tests are what keeps that true.
/// </para>
/// </remarks>
public sealed class DatumShiftNoticesTests
{
    private static readonly Guid Layer = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Other = new("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task A_pair_on_one_datum_says_nothing()
    {
        // <b>The test that keeps the list worth reading.</b> 4326 to 3857 is a closed formula
        // on one datum and is the most common transformation this server performs; recording
        // it would bury the one row an operator needs under every row they do not.
        DatumShiftNotices notices = new();
        Recorder log = new();

        await notices.NoteAsync(
            Layer, "parcels", 4326, 3857, new Projector(shift: false), log, default);

        Assert.Empty(notices.Report());
        Assert.Empty(log.Lines);
    }

    [Fact]
    public async Task A_datum_crossing_is_recorded_and_said_once()
    {
        DatumShiftNotices notices = new();
        Recorder log = new();
        Projector projector = new(shift: true);

        for (int i = 0; i < 6; i++)
        {
            await notices.NoteAsync(Layer, "tm30native", 5254, 4326, projector, log, default);
        }

        DatumShiftNotices.Notice one = Assert.Single(notices.Report());

        Assert.Equal("tm30native", one.Layer);
        Assert.Equal(5254, one.StoredAs);
        Assert.Equal(4326, one.ServedAs);
        Assert.True(one.CrossesDatum);

        // <b>Once, and the six calls cost one question.</b> A warning that repeats on every
        // request is a warning an operator filters out, and then the channel is gone.
        Assert.Single(log.Lines);
        Assert.Equal(1, projector.Asked);
    }

    [Fact]
    public async Task A_datum_that_could_not_be_read_is_recorded_rather_than_assumed_fine()
    {
        // <b>Null is not false.</b> A reference whose WKT names no datum is precisely the
        // case somebody should look at; treating *could not tell* as *fine* is how D-32's
        // failure stays invisible, which is the whole complaint.
        DatumShiftNotices notices = new();
        Recorder log = new();

        await notices.NoteAsync(
            Layer, "imported", 900913, 4326, new Projector(shift: null), log, default);

        DatumShiftNotices.Notice one = Assert.Single(notices.Report());

        Assert.False(one.CrossesDatum);
        Assert.NotEmpty(one.Caution);
        Assert.Single(log.Lines);
    }

    [Fact]
    public async Task The_same_layer_in_two_references_is_two_notices()
    {
        // <b>The pair is the unit, because it is what an operator acts on.</b> *This layer,
        // served as that reference* is a sentence somebody can check grids against.
        DatumShiftNotices notices = new();
        Recorder log = new();
        Projector projector = new(shift: true);

        await notices.NoteAsync(Layer, "tm30native", 5254, 4326, projector, log, default);
        await notices.NoteAsync(Layer, "tm30native", 5254, 3857, projector, log, default);

        Assert.Equal(2, notices.Report().Count);
        Assert.Equal(2, log.Lines.Count);
    }

    [Fact]
    public async Task A_transform_into_the_same_reference_asks_nothing()
    {
        // The projector throws if consulted, so this fails rather than passes quietly if the
        // early return goes away.
        DatumShiftNotices notices = new();

        await notices.NoteAsync(
            Layer, "parcels", 4326, 4326, new Projector(shift: true, refuse: true),
            new Recorder(), default);

        Assert.Empty(notices.Report());
    }

    [Fact]
    public async Task A_projector_that_fails_does_not_fail_the_request_and_records_nothing()
    {
        // <b>D-152's shape, and the reason this is worth a test.</b> The last time a cosmetic
        // check could propagate out of the thing it was commenting on, one unreadable
        // credential stopped the whole server from starting. An aside must not be able to
        // fail a request that is about to answer correctly.
        DatumShiftNotices notices = new();
        Recorder log = new();
        Projector projector = new(shift: true, refuse: true);

        await notices.NoteAsync(Layer, "tm30native", 5254, 4326, projector, log, default);

        Assert.Empty(notices.Report());

        // <b>And nothing is remembered</b>, so an outage does not become a permanent
        // *this pair is fine*: the next request asks again.
        projector.Recover();
        await notices.NoteAsync(Layer, "tm30native", 5254, 4326, projector, log, default);

        Assert.Single(notices.Report());
    }

    [Fact]
    public async Task Cancellation_is_not_swallowed()
    {
        // <b>The one exception to the rule above.</b> A cancelled request is the caller
        // leaving, not a failure to report — swallowing it would make this the one place a
        // shutdown waits on a projection database.
        DatumShiftNotices notices = new();
        using CancellationTokenSource source = new();
        await source.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            notices.NoteAsync(
                Layer, "tm30native", 5254, 4326, new Projector(shift: true, cancel: true),
                new Recorder(), source.Token));
    }

    [Fact]
    public async Task The_register_is_bounded_and_says_when_it_stops()
    {
        // <b>Half the key space is the caller's.</b> A client naming ten thousand SRIDs would
        // otherwise grow this without limit, which is the shape
        // `EveryLongLivedCacheIsBoundedTests` exists to refuse.
        DatumShiftNotices notices = new();
        Recorder log = new();
        Projector projector = new(shift: true);

        for (int i = 0; i < DatumShiftNotices.Ceiling + 50; i++)
        {
            await notices.NoteAsync(Layer, "tm30native", 5254, 20000 + i, projector, log, default);
        }

        Assert.Equal(DatumShiftNotices.Ceiling, notices.Report().Count);
        Assert.True(notices.Truncated);
    }

    [Fact]
    public async Task The_report_reads_the_same_twice()
    {
        // An operator comparing two readings of this page should be comparing the contents,
        // not the dictionary's enumeration order.
        DatumShiftNotices notices = new();
        Recorder log = new();
        Projector projector = new(shift: true);

        await notices.NoteAsync(Other, "zzz", 5254, 4326, projector, log, default);
        await notices.NoteAsync(Layer, "aaa", 5254, 3857, projector, log, default);
        await notices.NoteAsync(Layer, "aaa", 5254, 4326, projector, log, default);

        Assert.Equal(
            [("aaa", 3857), ("aaa", 4326), ("zzz", 4326)],
            notices.Report().ConvertToPairs());
    }

    /// <summary>A projector that answers the one question and nothing else.</summary>
    private sealed class Projector(bool? shift, bool refuse = false, bool cancel = false)
        : IProjector
    {
        private bool _refuse = refuse;

        /// <summary>How many times the datum question was actually asked.</summary>
        public int Asked { get; private set; }

        public void Recover() => _refuse = false;

        public Task<ProjectionProvenance> DescribeAsync(
            int fromSrid, int toSrid, CancellationToken cancellationToken)
        {
            Asked++;

            if (cancel)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (_refuse)
            {
                throw new InvalidOperationException("the projection database is unreachable");
            }

            return Task.FromResult(new ProjectionProvenance(
                "test",
                Accuracy: null,
                DatumShift: shift,
                Caution: shift is false ? null : "a caution"));
        }

        public Task<(IReadOnlyList<Geometry> Projected, ProjectionProvenance Provenance)>
            ProjectAsync(
                IReadOnlyList<Geometry> geometries, int fromSrid, int toSrid,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException("the notice does not move geometry");

        public Task<bool> KnowsAsync(int srid, CancellationToken cancellationToken) =>
            throw new NotSupportedException("the notice does not ask this");

        /// <summary>This double knows no areas of use, which is a complete answer.</summary>
        public Task<Envelope?> DomainOfAsync(int srid, CancellationToken cancellationToken) =>
            Task.FromResult<Envelope?>(null);
    }

    /// <summary>An <see cref="ILogger"/> that keeps what it was told.</summary>
    private sealed class Recorder : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            // <b>Only the warnings.</b> `DatumShiftUnknown` is Debug and is an aside about an
            // aside; counting it would make the *said once* assertions count the wrong thing.
            if (logLevel >= LogLevel.Warning)
            {
                Lines.Add(formatter(state, exception));
            }
        }
    }
}

/// <summary>Reads a report as pairs, so the ordering assertion stays legible.</summary>
internal static class NoticeReading
{
    public static IReadOnlyList<(string Layer, int ServedAs)> ConvertToPairs(
        this IReadOnlyList<DatumShiftNotices.Notice> notices)
    {
        ArgumentNullException.ThrowIfNull(notices);

        List<(string, int)> pairs = new(notices.Count);

        foreach (DatumShiftNotices.Notice each in notices)
        {
            pairs.Add((each.Layer, each.ServedAs));
        }

        return pairs;
    }
}
