using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Host;
using Graticula.Platform.Jobs;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// The nudge that lets an idle worker wait half a minute without costing latency.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-110](../../docs/architecture-debt.md): two workers polling every two seconds held eight
/// database sessions open for ever, on a server doing nothing.</b> The repair is to back the poll
/// off when nothing is arriving — and a backed-off poll is a job that sits queued for up to half
/// a minute after somebody pressed Publish. This is what stops that being true for work enqueued
/// on the same node.
/// </para>
/// <para>
/// <b>Timed loosely and asserted on order, not on milliseconds.</b> A test that pins a wake to a
/// number of milliseconds is a test about the machine it runs on.
/// </para>
/// </remarks>
public sealed class JobSignalTests
{
    /// <summary>A wake ends a wait that would otherwise have run to its patience.</summary>
    [Fact]
    public async Task A_wake_ends_the_wait_early()
    {
        JobSignal signal = new();

        Stopwatch clock = Stopwatch.StartNew();

        Task<bool> waiting = signal.WaitAsync(
            JobKind.GeodatabaseImport, TimeSpan.FromSeconds(30), CancellationToken.None);

        signal.Wake(JobKind.GeodatabaseImport);

        Assert.True(await waiting, "the wait timed out instead of being woken");

        Assert.True(
            clock.Elapsed < TimeSpan.FromSeconds(5),
            $"the wake took {clock.Elapsed.TotalSeconds:F1}s of a thirty-second patience. A job "
            + "enqueued here has to start now, or the backoff D-110 asks for is latency somebody "
            + "notices.");
    }

    /// <summary>A wait nobody wakes ends by itself, and says so.</summary>
    /// <remarks>
    /// <b>The false is the fallback.</b> A second node's work arrives with no signal at all, so
    /// the worker has to come back and ask — that is what the poll is still for.
    /// </remarks>
    [Fact]
    public async Task A_wait_nobody_wakes_times_out()
    {
        JobSignal signal = new();

        Assert.False(
            await signal.WaitAsync(
                JobKind.GeodatabaseInspect, TimeSpan.FromMilliseconds(50), CancellationToken.None));
    }

    /// <summary>A wake for one kind does not release a worker waiting for another.</summary>
    /// <remarks>
    /// <b>Two workers share this object, and waking the wrong one is a spin.</b> The inspector
    /// would claim, find nothing, and reset its patience to two seconds — putting back exactly
    /// the poll this repair removes.
    /// </remarks>
    [Fact]
    public async Task A_wake_reaches_only_its_own_kind()
    {
        JobSignal signal = new();

        signal.Wake(JobKind.GeodatabaseImport);

        Assert.False(
            await signal.WaitAsync(
                JobKind.GeodatabaseInspect, TimeSpan.FromMilliseconds(50), CancellationToken.None));

        Assert.True(
            await signal.WaitAsync(
                JobKind.GeodatabaseImport, TimeSpan.FromSeconds(5), CancellationToken.None));
    }

    /// <summary>
    /// Wakes do not accumulate while nobody is waiting.
    /// </summary>
    /// <remarks>
    /// <b>Otherwise three enqueues would let the next idle worker through three empty claims.</b>
    /// One wake means *there is something*, and the worker finds out how much by claiming until
    /// there is not.
    /// </remarks>
    [Fact]
    public async Task Wakes_do_not_pile_up()
    {
        JobSignal signal = new();

        signal.Wake(JobKind.GeodatabaseImport);
        signal.Wake(JobKind.GeodatabaseImport);
        signal.Wake(JobKind.GeodatabaseImport);

        Assert.True(
            await signal.WaitAsync(
                JobKind.GeodatabaseImport, TimeSpan.FromSeconds(5), CancellationToken.None));

        Assert.False(
            await signal.WaitAsync(
                JobKind.GeodatabaseImport, TimeSpan.FromMilliseconds(50), CancellationToken.None));
    }

    /// <summary>Cancellation ends the wait without throwing at the caller.</summary>
    /// <remarks>
    /// <b>The workers' loops end on their own condition.</b> A throw here would make stopping the
    /// server produce a logged exception per worker, which is noise at exactly the moment
    /// somebody is reading the log.
    /// </remarks>
    [Fact]
    public async Task Cancelling_ends_the_wait_quietly()
    {
        JobSignal signal = new();

        using CancellationTokenSource stopping = new();

        Task<bool> waiting = signal.WaitAsync(
            JobKind.GeodatabaseImport, TimeSpan.FromSeconds(30), stopping.Token);

        await stopping.CancelAsync();

        Assert.False(await waiting);
    }
}
