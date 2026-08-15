using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;

namespace GisServer.Conformance.Tests;

/// <summary>
/// One cold tile, many callers, one build.
/// </summary>
/// <remarks>
/// <para>
/// <b>Measured before the fix existed: twelve simultaneous callers produced
/// twelve builds.</b> Every one missed the cache, every one queried the
/// datastore, and eleven results were written over each other. That is the
/// moment a map is first opened, or a cache is cleared, or a layer is
/// republished — precisely when the datastore is least able to absorb the same
/// work N times.
/// </para>
/// <para>
/// <b>An earlier check with curl in a shell loop reported one miss and eleven
/// hits and was wrong.</b> Process start-up staggered the requests by enough for
/// the first to finish and warm the cache, so the harness measured its own
/// latency and reported coalescing that did not exist. This test opens its
/// connections first and releases every request from a gate.
/// </para>
/// </remarks>
public sealed class TileHerdConformanceTests : ArcGisClient
{
    private const string ServiceVariable = "GISSERVER_TEST_TILE_SERVICE";

    private const int Callers = 12;

    private static string? Configured => Environment.GetEnvironmentVariable(ServiceVariable);

    private async Task<string> RequireTileServiceAsync()
    {
        await RequireServerAsync();

        Assert.False(
            string.IsNullOrWhiteSpace(Configured),
            $"{ServiceVariable} is not set, so these tests FAIL rather than skip.");

        return Configured!.Trim('/');
    }

    /// <summary>
    /// A tile nobody has asked for, so the run is genuinely cold.
    /// </summary>
    /// <remarks>
    /// <b>Derived from the clock, deliberately.</b> A fixed address is cold once
    /// and warm for every run afterwards, so the test would pass for the wrong
    /// reason from the second run onward. Zoom 20 is deep enough that the tile is
    /// almost certainly empty — which still builds, still caches a zero-length
    /// marker, and exercises the same path faster.
    /// </remarks>
    private static (int Z, int X, int Y) ColdTile()
    {
        long tick = DateTime.UtcNow.Ticks;

        const int Z = 20;
        int span = 1 << Z;

        return (Z, (int)(tick % span), (int)(tick / 7 % span));
    }

    /// <summary>
    /// However many callers race for a cold tile, exactly one builds it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The assertion is on the number of builds, not on the number that
    /// coalesced</b>, and that is what keeps it from being flaky. If the runtime
    /// happens to stagger the requests, the losers report <c>HIT</c> instead of
    /// <c>COALESCED</c> — they still did not build. Either way exactly one caller
    /// made the datastore work, and before <c>TileSingleFlight</c> that number
    /// was twelve.
    /// </para>
    /// <para>
    /// So a regression here is unambiguous: the count goes above one.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Twelve_callers_racing_for_one_cold_tile_cause_one_build()
    {
        string root = await RequireServerAsync();
        string service = await RequireTileServiceAsync();

        (int z, int x, int y) = ColdTile();

        Uri tile = new(string.Create(
            CultureInfo.InvariantCulture,
            $"{root}/rest/services/{service}/VectorTileServer/tile/{z}/{y}/{x}.pbf"));

        using HttpClientHandler handler = new()
        {
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            MaxConnectionsPerServer = Callers,
        };

        using HttpClient http = new(handler);

        // Warm the connection pool and the route on a different address, so the
        // race below is a race to build a tile and not a race to open a socket.
        using (HttpResponseMessage _ = await http.GetAsync(new Uri(
            $"{root}/rest/services/{service}/VectorTileServer")))
        {
        }

        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        List<Task<string?>> callers = [.. Enumerable.Range(0, Callers).Select(async _ =>
        {
            await gate.Task.ConfigureAwait(false);

            using HttpResponseMessage response = await http.GetAsync(tile);

            return response.Headers.TryGetValues("X-Tile-Cache", out IEnumerable<string>? v)
                ? v.FirstOrDefault()
                : null;
        })];

        gate.SetResult();

        string?[] dispositions = await Task.WhenAll(callers);

        int built = dispositions.Count(d => d == "MISS");

        Assert.True(
            built == 1,
            $"{built} of {Callers} callers built the same cold tile. Expected exactly one. "
            + $"Dispositions: {string.Join(", ", dispositions.GroupBy(d => d).Select(g => $"{g.Key}={g.Count()}"))}");

        // And nobody was refused or left without bytes.
        Assert.All(dispositions, d => Assert.Contains(d, (string?[])["MISS", "COALESCED", "HIT"]));
    }

    /// <summary>
    /// A tile already cached is a hit for everybody, with no build at all.
    /// </summary>
    /// <remarks>
    /// The other half of the property: coalescing must not have turned every
    /// warm read into a rendezvous. If a warm tile ever reports
    /// <c>COALESCED</c>, the cache is being bypassed and the single-flight table
    /// has become the cache.
    /// </remarks>
    [Fact]
    public async Task A_warm_tile_is_a_hit_for_everybody()
    {
        string root = await RequireServerAsync();
        string service = await RequireTileServiceAsync();

        (int z, int x, int y) = ColdTile();

        Uri tile = new(string.Create(
            CultureInfo.InvariantCulture,
            $"{root}/rest/services/{service}/VectorTileServer/tile/{z}/{y}/{x}.pbf"));

        using HttpClientHandler handler = new()
        {
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };

        using HttpClient http = new(handler);

        using (HttpResponseMessage first = await http.GetAsync(tile))
        {
            Assert.True(first.IsSuccessStatusCode || (int)first.StatusCode == 204);
        }

        string?[] dispositions = await Task.WhenAll(
            Enumerable.Range(0, Callers).Select(async _ =>
            {
                using HttpResponseMessage response = await http.GetAsync(tile);

                return response.Headers.TryGetValues("X-Tile-Cache", out IEnumerable<string>? v)
                    ? v.FirstOrDefault()
                    : null;
            }));

        Assert.All(dispositions, d => Assert.Equal("HIT", d));
    }
}
