using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Tiles;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// A map's cold tile takes a permit from <see cref="ConnectionBudget"/>, once per build — D-277.
/// </summary>
/// <remarks>
/// <para>
/// <b>Against the route's own cache-or-build step and a real budget, with no database.</b>
/// <c>VectorTileEndpoints.CachedOrBuiltAsync</c> is the tile route's per-layer body minus the describe
/// and the key, and the three things D-277's repair turns on are decided inside it: the cache is read
/// before any permit is asked for, the permit is taken inside the shared build, and a refusal is the
/// build's outcome. A fake source and a fake cache are the whole harness; the budget is the real one,
/// so a refusal here is the exception the exception handler answers with 503.
/// </para>
/// <para>
/// <b>What this does not cover</b> is the wiring — that the route passes
/// <c>LayerConnections.AdmitTileBuildAsync</c> — which needs a server and a data source, and a tile
/// 503 under an exhausted budget is not something a conformance fixture can provoke on demand.
/// </para>
/// </remarks>
public sealed class TileBuildAdmissionTests
{
    private static readonly TimeSpan Brief = TimeSpan.FromMilliseconds(150);

    private static readonly TileAddress Address = new(12, 2385, 1536);

    private static readonly TileCacheKey Key = new(Guid.NewGuid(), "abcd1234", Address);

    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    [Fact]
    public async Task A_tile_the_cache_answers_takes_no_permit()
    {
        using ConnectionBudget budget = new(worker: 1, perSource: 1, Brief);
        Admission admission = new(budget);
        MemoryCache cache = new();
        await cache.WriteAsync(Key, [1, 2, 3], CancellationToken.None);

        // Every permit is held elsewhere: had the cached tile asked for one, it would be refused.
        using ConnectionBudget.Lease elsewhere = await budget.EnterAsync(Admission.Source, CancellationToken.None);

        VectorTileEndpoints.LayerPart part = await PartAsync(cache, new(), new CountingSource(), admission.AdmitAsync);

        Assert.Equal(VectorTileEndpoints.PartCame.Cached, part.Came);
        Assert.Equal(0, admission.Asked);
    }

    /// <remarks>
    /// <b>The §2c guarantee survives the permit</b>: twelve callers racing for one cold tile cause one
    /// build and one permit. Before the build is released every caller has joined it, so the eleven
    /// who did not build it are waiting on its task, not on the budget.
    /// </remarks>
    [Fact]
    public async Task Twelve_callers_racing_for_one_cold_tile_take_one_permit()
    {
        using ConnectionBudget budget = new(worker: 1, perSource: 1, Brief);
        Admission admission = new(budget);
        MemoryCache cache = new();
        TileSingleFlight building = new();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CountingSource source = new(release.Task);

        Task<VectorTileEndpoints.LayerPart>[] callers =
            [.. Enumerable.Range(0, 12).Select(_ => Task.Run(() => PartAsync(cache, building, source, admission.AdmitAsync)))];

        // Hold the build until every caller is either building or waiting on the build.
        await WaitUntil(() => source.Started == 1 && building.InFlight == 1);
        await Task.Delay(100);
        release.SetResult();

        VectorTileEndpoints.LayerPart[] parts = await Task.WhenAll(callers);

        Assert.Equal(1, source.Started);
        Assert.Equal(1, admission.Asked);
        Assert.All(parts, part => Assert.Equal(new byte[] { 7, 7, 7 }, part.Bytes));
        Assert.Equal(1, parts.Count(part => part.Came == VectorTileEndpoints.PartCame.Built));
    }

    /// <remarks>
    /// <b>A refused build refuses everyone waiting on it, and nobody asks the budget again.</b> Each
    /// waiter taking its own turn at the budget would turn one cold tile under load into N refusals
    /// counted against the queue — the herd §2c removed, arriving at the limiter instead.
    /// </remarks>
    [Fact]
    public async Task An_exhausted_budget_refuses_the_build_once_and_every_waiter_with_it()
    {
        using ConnectionBudget budget = new(worker: 4, perSource: 1, Brief, waitersPerPermit: 64);
        Admission admission = new(budget);
        MemoryCache cache = new();
        TileSingleFlight building = new();
        CountingSource source = new();

        using ConnectionBudget.Lease elsewhere = await budget.EnterAsync(Admission.Source, CancellationToken.None);

        Task<VectorTileEndpoints.LayerPart>[] callers =
            [.. Enumerable.Range(0, 8).Select(_ => Task.Run(() => PartAsync(cache, building, source, admission.AdmitAsync)))];

        foreach (Task<VectorTileEndpoints.LayerPart> caller in callers)
        {
            await Assert.ThrowsAsync<ConnectionBudgetFullException>(() => caller);
        }

        Assert.Equal(0, source.Started);
        Assert.True(
            admission.Asked <= callers.Length && admission.Asked >= 1,
            $"The budget was asked {admission.Asked} times.");

        // Callers that arrived while the refused build was in flight shared it. A caller that arrived
        // after it had already been refused starts a build of its own, and is refused on its own.
        Assert.Equal(0, building.InFlight);
        Assert.Equal(CachedTile.Miss.Outcome, (await cache.ReadAsync(Key, Lifetime, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Callers_that_join_a_refused_build_are_refused_without_asking_the_budget()
    {
        using ConnectionBudget budget = new(worker: 4, perSource: 1, TimeSpan.FromMilliseconds(400), waitersPerPermit: 64);
        Admission admission = new(budget);
        TileSingleFlight building = new();
        CountingSource source = new();

        using ConnectionBudget.Lease elsewhere = await budget.EnterAsync(Admission.Source, CancellationToken.None);

        // The first caller starts the build, which waits 400 ms for a permit it will not get; the
        // others arrive inside that window and join it.
        Task<VectorTileEndpoints.LayerPart> first = PartAsync(new MemoryCache(), building, source, admission.AdmitAsync);
        await WaitUntil(() => building.InFlight == 1);

        Task<VectorTileEndpoints.LayerPart>[] joined =
            [.. Enumerable.Range(0, 6).Select(_ => PartAsync(new MemoryCache(), building, source, admission.AdmitAsync))];

        await Assert.ThrowsAsync<ConnectionBudgetFullException>(() => first);

        foreach (Task<VectorTileEndpoints.LayerPart> caller in joined)
        {
            await Assert.ThrowsAsync<ConnectionBudgetFullException>(() => caller);
        }

        Assert.Equal(1, admission.Asked);
        Assert.Equal(0, source.Started);
    }

    /// <remarks>
    /// <b>The refusal is answered as a query's is</b> — the same exception, so the same status and
    /// the same retry signal (<c>ErrorResponse</c>), which is what the owner accepted a map may see.
    /// </remarks>
    [Fact]
    public void A_refused_tile_is_a_503_with_a_retry_after()
    {
        ConnectionBudgetFullException refused = new("busy");

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, ErrorResponse.Classify(refused).Status);
        Assert.NotNull(ErrorResponse.RetryAfterFor(refused));
    }

    [Fact]
    public async Task A_built_tile_gives_its_permit_back()
    {
        using ConnectionBudget budget = new(worker: 1, perSource: 1, Brief);
        Admission admission = new(budget);

        await PartAsync(new MemoryCache(), new(), new CountingSource(), admission.AdmitAsync);

        // The only permit there is: taken again at once, so the build released it.
        using ConnectionBudget.Lease again = await budget.EnterAsync(Admission.Source, CancellationToken.None);
    }

    private static Task<VectorTileEndpoints.LayerPart> PartAsync(
        ITileCache cache,
        TileSingleFlight building,
        ITileSource source,
        Func<CancellationToken, ValueTask<IDisposable>> admit) =>
        VectorTileEndpoints.CachedOrBuiltAsync(
            Key, Address, "layer", Lifetime, revalidate: false, cache, building, () => source, admit,
            CancellationToken.None);

    private static async Task WaitUntil(Func<bool> condition)
    {
        DateTime until = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "The condition did not hold within ten seconds.");
            await Task.Delay(5);
        }
    }

    /// <summary><c>LayerConnections.AdmitTileBuildAsync</c>'s last step, counted.</summary>
    private sealed class Admission(ConnectionBudget budget)
    {
        public const string Source = "Host=db;Database=tiles";

        private int _asked;

        public int Asked => Volatile.Read(ref _asked);

        public async ValueTask<IDisposable> AdmitAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _asked);
            return await budget.EnterAsync(Source, cancellationToken);
        }
    }

    private sealed class CountingSource(Task? gate = null) : ITileSource
    {
        private int _started;

        public int Started => Volatile.Read(ref _started);

        public async Task<byte[]> BuildAsync(TileAddress address, string layerName, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _started);

            if (gate is not null)
            {
                await gate;
            }

            return [7, 7, 7];
        }
    }

    private sealed class MemoryCache : ITileCache
    {
        private readonly ConcurrentDictionary<TileCacheKey, byte[]> _held = new();

        public Task<CachedTile> ReadAsync(TileCacheKey key, TimeSpan lifetime, CancellationToken cancellationToken) =>
            Task.FromResult(_held.TryGetValue(key, out byte[]? bytes)
                ? new CachedTile(bytes.Length == 0 ? TileCacheOutcome.Empty : TileCacheOutcome.Hit, bytes, DateTimeOffset.UtcNow)
                : CachedTile.Miss);

        public Task WriteAsync(TileCacheKey key, byte[] tile, CancellationToken cancellationToken)
        {
            _held[key] = tile;
            return Task.CompletedTask;
        }

        public int Purge(Guid layerId) => 0;

        public (int Entries, long Bytes) Report(Guid? layerId) => (_held.Count, _held.Values.Sum(b => (long)b.Length));
    }
}
