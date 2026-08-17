using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Tiles;

namespace Graticula.Host;

/// <summary>
/// One build per cold tile, however many callers ask for it at once.
/// </summary>
/// <remarks>
/// <para>
/// <b>Measured before it was written: twelve simultaneous callers produced
/// twelve builds.</b> Every one missed the cache, every one queried the
/// datastore, and eleven of the twelve results were thrown away by being written
/// over each other. A cold popular tile — the moment a map is first opened, or a
/// cache is cleared, or a layer is republished — is exactly when the datastore
/// is asked to do the same work N times.
/// </para>
/// <para>
/// <b>An earlier check with curl in a shell loop reported one miss and eleven
/// hits, and that was an artefact.</b> Process start-up staggered the requests
/// by enough for the first to finish and warm the cache. The measurement that
/// found the real behaviour opens every socket first and releases the threads
/// from a barrier. A concurrency test that does not synchronise its clients is
/// testing its own start-up latency.
/// </para>
/// <para>
/// <b>This is deliberately node-local.</b> Across nodes the same herd arrives
/// once per node, and the answer there is a caching reverse proxy — tiles already
/// carry <c>Cache-Control: public, max-age=3600</c>, and every real deployment
/// has a proxy in front for TLS. A distributed lock would be a dependency bought
/// to solve the smaller half of the problem
/// (<see href="../../docs/adr/ADR-029-affinity-routing-is-not-the-default.md">ADR-029</see>).
/// </para>
/// </remarks>
internal sealed class TileSingleFlight
{
    private readonly ConcurrentDictionary<TileCacheKey, Task<byte[]>> _building = new();

    /// <summary>How the caller got its bytes.</summary>
    /// <param name="Bytes">The tile.</param>
    /// <param name="Built">
    /// True if this caller did the work; false if it waited for somebody else's.
    /// Reported as <c>MISS</c> or <c>COALESCED</c>, which is what makes the
    /// behaviour visible in a log rather than only in a benchmark.
    /// </param>
    public readonly record struct Result(byte[] Bytes, bool Built);

    /// <summary>
    /// Builds the tile, or waits for the build already running.
    /// </summary>
    /// <param name="key">The tile, including the fingerprint — a layer whose
    /// shape changed is a different key and must not wait on the old build.</param>
    /// <param name="build">How to make it. Runs at most once per key.</param>
    /// <param name="cancellationToken">The <em>caller's</em> cancellation, which
    /// abandons the wait without abandoning the build.</param>
    /// <returns>The bytes, and whether this caller produced them.</returns>
    public async Task<Result> BuildAsync(
        TileCacheKey key,
        Func<Task<byte[]>> build,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(build);

        // RunContinuationsAsynchronously, or the caller that finishes the build
        // runs every waiter's continuation on its own thread before returning —
        // which turns one slow tile into one thread doing eleven responses.
        TaskCompletionSource<byte[]> mine = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<byte[]> shared = _building.GetOrAdd(key, mine.Task);

        if (!ReferenceEquals(shared, mine.Task))
        {
            // <b>Somebody else is building it, and their build does not belong
            // to us.</b> WaitAsync gives this caller its own cancellation: a
            // browser that navigates away stops waiting, and the build carries
            // on for everybody still here.
            return new Result(await shared.WaitAsync(cancellationToken).ConfigureAwait(false),
                Built: false);
        }

        try
        {
            // <b>The caller's token is deliberately not passed in.</b> The build
            // is shared, so cancelling it because the first caller left would
            // fail the other eleven — and they cannot retry, because they are
            // waiting on this exact task. It is bounded anyway: the statement
            // timeout (D-08) is what stops a build running forever.
            byte[] bytes = await build().ConfigureAwait(false);

            mine.SetResult(bytes);
            return new Result(bytes, Built: true);
        }
        catch (Exception e)
        {
            // Everyone waiting sees the same failure rather than hanging, and
            // the finally below clears the key so the next request tries again
            // instead of inheriting a cached exception forever.
            mine.SetException(e);
            throw;
        }
        finally
        {
            // <b>Removed on completion, always.</b> A pyramid has millions of
            // addresses; a dictionary that only grows is a slow leak that looks
            // like a memory problem months later, in a component nobody
            // suspects. A caller arriving between the result and this removal
            // simply awaits a task that is already finished.
            _building.TryRemove(key, out _);
        }
    }

    /// <summary>How many builds are in flight, for the health document.</summary>
    public int InFlight => _building.Count;
}
