using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Geometries;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Tiles;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// A tile whose source refuses to build it is served from its expired copy, marked, for a bounded time —
/// ADR-010 §5.1a, owner decision 2026-09-29, D-278.
/// </summary>
/// <remarks>
/// <para>
/// <b>Against the route's own steps and a real cache on a real disk, with no database.</b> The build is
/// <c>VectorTileEndpoints.CachedOrBuiltAsync</c> with an admission that throws one of the three refusals
/// <c>LayerConnections.AdmitTileBuildAsync</c> throws; the stand-in is <c>StaleOrNothingAsync</c> over a
/// <see cref="FileSystemTileCache"/> on a fake clock; the response is <c>RespondAsync</c> writing into a
/// <see cref="DefaultHttpContext"/>. <c>ServeTileAsync</c> is those three in a loop, which is why they were split.
/// </para>
/// <para>
/// <b>What this does not cover</b> is the wiring to a real source — that a quiesced datastore reaches the
/// stand-in with its remembered shape — which the conformance suite's
/// <c>AStaleTileStandsInForARefusedOneTests</c> does against a live server.
/// </para>
/// </remarks>
public sealed class StaleWhileErrorTests : IDisposable
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    private static readonly TimeSpan Day = TimeSpan.FromHours(24);

    private static readonly TileAddress Address = new(12, 2385, 1536);

    private static readonly Guid LayerId = Guid.NewGuid();

    private static readonly TileCacheKey Key = new(LayerId, "abcd1234", Address);

    private static readonly byte[] Bytes = [0x1a, 0x02, 0x08, 0x02, 0x1a, 0x02, 0x08, 0x03];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "gis-stale-" + Guid.NewGuid().ToString("N"));

    // Three hours ago, so the tile written now is three hours old by the wall clock `Age` is measured on.
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow - TimeSpan.FromHours(3));

    private readonly FileSystemTileCache _cache;

    public StaleWhileErrorTests()
    {
        _cache = new FileSystemTileCache(_root, 1_000_000, 1_000_000, Lifetime, _clock, NullLoggerFactory.Instance);
    }

    public static TheoryData<string> Refusals => new() { "budget", "breaker", "quiesce" };

    private static Exception Refusal(string which) => which switch
    {
        "budget" => new ConnectionBudgetFullException("Every connection to this source is in use."),
        "breaker" => new SourceUnreachableException(),
        _ => new SourceQuiescedException("An administrator has quiesced this source.", DateTimeOffset.UtcNow.AddMinutes(5)),
    };

    /// <remarks>
    /// <b>Each of the three refusals, and the headers a stale tile goes out with</b>: 200 with the same bytes,
    /// <c>X-Tile-Cache: STALE</c>, the real <c>Age</c> — past the lifetime — a <c>max-age</c> of that age plus a
    /// minute, so a downstream cache that honours <c>Age</c> keeps it for the minute, and the weak ETag a fresh copy
    /// of the same bytes carries.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task A_refused_build_is_answered_from_the_expired_copy_marked_stale(string which)
    {
        await ExpiredCopyAsync();
        Exception refusal = Refusal(which);

        (VectorTileEndpoints.LayerPart part, Exception? refused) = await PartAsync(refusal);

        Assert.Equal(VectorTileEndpoints.PartCame.Stale, part.Came);
        Assert.Same(refusal, refused);
        Assert.Equal(Bytes, part.Bytes);

        DefaultHttpContext context = await RespondAsync(part);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("STALE", context.Response.Headers["X-Tile-Cache"].ToString());
        Assert.Equal(ETagOf(Bytes), context.Response.Headers.ETag.ToString());
        Assert.Equal(Bytes, ((MemoryStream)context.Response.Body).ToArray());

        long age = AgeOf(context);
        Assert.True(age >= Lifetime.TotalSeconds, $"A stale tile said `Age: {age}`, younger than its own lifetime.");

        // RFC 9111 §4.2.3: fresh downstream while max-age exceeds the current age, which starts at `Age` — so a
        // minute past the age sent is a minute a proxy or a browser really keeps it.
        Assert.Equal($"public, max-age={age + 60}", context.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task A_stale_tile_the_caller_holds_is_a_304_that_still_says_stale()
    {
        await ExpiredCopyAsync();

        (VectorTileEndpoints.LayerPart part, _) = await PartAsync(new SourceUnreachableException());

        DefaultHttpContext context = await RespondAsync(part, ifNoneMatch: ETagOf(Bytes));

        Assert.Equal(StatusCodes.Status304NotModified, context.Response.StatusCode);
        Assert.Equal("STALE", context.Response.Headers["X-Tile-Cache"].ToString());
        Assert.Equal(0, context.Response.Body.Length);
    }

    /// <remarks><b>V-56 holds through an outage</b>: a layer a browser must revalidate is still <c>no-cache</c>.</remarks>
    [Fact]
    public async Task A_stale_tile_of_an_editable_layer_still_says_no_cache()
    {
        await ExpiredCopyAsync();

        (VectorTileEndpoints.LayerPart part, _) = await PartAsync(new SourceUnreachableException(), revalidate: true);

        DefaultHttpContext context = await RespondAsync(part);

        Assert.Equal("public, no-cache", context.Response.Headers.CacheControl.ToString());
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task Past_its_stale_limit_the_refusal_is_answered_as_before(string which)
    {
        await ExpiredCopyAsync();
        _clock.Advance(Day);

        Exception refusal = Refusal(which);

        Exception thrown = await Assert.ThrowsAsync(refusal.GetType(), () => PartAsync(refusal));
        Assert.Same(refusal, thrown);
    }

    /// <remarks>
    /// <b>A purged tile stays purged through an outage</b> — ADR-010 §5.1's wrong class. An edit, a refresh, a
    /// data-source move and a tiling-scheme switch all end in <c>ITileCache.Purge</c>, which deletes the files.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task After_a_purge_the_refusal_is_answered_as_before(string which)
    {
        await ExpiredCopyAsync();
        _cache.Purge(LayerId);

        Exception refusal = Refusal(which);

        Assert.Same(refusal, await Assert.ThrowsAsync(refusal.GetType(), () => PartAsync(refusal)));
    }

    /// <remarks>
    /// <b>Only an outage is served stale.</b> A failure that is the tile's own — a bad statement, a bug — is the
    /// fault an error exists to report, and the stand-in is not even asked.
    /// </remarks>
    [Fact]
    public async Task A_failure_that_is_not_an_outage_is_not_served_stale()
    {
        await ExpiredCopyAsync();
        bool asked = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() => VectorTileEndpoints.PartOrStandInAsync(
            () => Task.FromException<VectorTileEndpoints.LayerPart>(new InvalidOperationException("a bug")),
            () =>
            {
                asked = true;
                return Task.FromResult<VectorTileEndpoints.LayerPart?>(null);
            }));

        Assert.False(asked);
    }

    /// <remarks>
    /// <b>A seed and an export call the build without the stand-in</b>, so a refusal reaches them as it always
    /// did — the seed pauses, the export does not package a copy it did not build.
    /// </remarks>
    [Fact]
    public async Task The_build_alone_never_takes_a_stale_copy()
    {
        await ExpiredCopyAsync();

        await Assert.ThrowsAsync<SourceUnreachableException>(() => BuildAsync(new SourceUnreachableException()));
    }

    /// <remarks>
    /// <b>A fresh copy found through the same door is a hit</b>: a quiesced source refuses its describe before its
    /// build, so a tile still inside its lifetime reaches the cache this way too, and is not stale.
    /// </remarks>
    [Fact]
    public async Task A_fresh_copy_standing_in_is_a_hit_not_stale()
    {
        await _cache.WriteAsync(Key, Bytes, CancellationToken.None);

        (VectorTileEndpoints.LayerPart part, Exception? refused) = await VectorTileEndpoints.PartOrStandInAsync(
            () => Task.FromException<VectorTileEndpoints.LayerPart>(new SourceQuiescedException("quiesced", DateTimeOffset.UtcNow)),
            () => VectorTileEndpoints.StaleOrNothingAsync(Key, Lifetime, Day, false, _cache, CancellationToken.None));

        Assert.Equal(VectorTileEndpoints.PartCame.Cached, part.Came);
        Assert.NotNull(refused);

        DefaultHttpContext context = await RespondAsync(part);

        Assert.Equal("HIT", context.Response.Headers["X-Tile-Cache"].ToString());
        Assert.Equal("public, max-age=3600", context.Response.Headers.CacheControl.ToString());
    }

    /// <remarks><b>One stale part makes the tile stale</b>, whatever its other layers were.</remarks>
    [Fact]
    public async Task One_stale_layer_makes_the_whole_tile_stale()
    {
        await ExpiredCopyAsync();

        (VectorTileEndpoints.LayerPart stale, _) = await PartAsync(new SourceUnreachableException());
        VectorTileEndpoints.LayerPart built = new([0x1a, 0x00], VectorTileEndpoints.PartCame.Built, null, Lifetime, false);

        DefaultHttpContext context = NewContext();
        await VectorTileEndpoints.RespondAsync(
            context, [(Layer(), built), (Layer(), stale)], Lifetime, CancellationToken.None);

        Assert.Equal("STALE", context.Response.Headers["X-Tile-Cache"].ToString());
        Assert.Equal($"public, max-age={AgeOf(context) + 60}", context.Response.Headers.CacheControl.ToString());
    }

    private static long AgeOf(DefaultHttpContext context) =>
        long.Parse(context.Response.Headers.Age.ToString(), System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void Only_the_three_outages_count_as_a_refused_source()
    {
        Assert.True(VectorTileEndpoints.SourceRefused(new ConnectionBudgetFullException("busy")));
        Assert.True(VectorTileEndpoints.SourceRefused(new SourceUnreachableException()));
        Assert.True(VectorTileEndpoints.SourceRefused(new SourceQuiescedException("quiesced", DateTimeOffset.UtcNow)));
        Assert.True(VectorTileEndpoints.SourceRefused(new System.Net.Sockets.SocketException()));

        Assert.False(VectorTileEndpoints.SourceRefused(new InvalidOperationException()));
        Assert.False(VectorTileEndpoints.SourceRefused(new OperationCanceledException()));
    }

    [Fact]
    public void A_service_with_a_quota_carries_it_with_every_layer_and_one_without_carries_nothing()
    {
        PublishedService none = new(
            Guid.NewGuid(), "roads", null, "FeatureServer", null, null,
            SharingScope.Public, ServiceStatus.Started, [Layer(), Layer()]);

        Assert.Null(VectorTileEndpoints.QuotaOf(none));

        PublishedService some = new(
            Guid.NewGuid(), "roads", null, "FeatureServer", null, null,
            SharingScope.Public, ServiceStatus.Started, [Layer(), Layer()])
        {
            TileCacheQuotaMegabytes = 3,
        };

        TileCacheQuota quota = VectorTileEndpoints.QuotaOf(some)!;

        Assert.Equal(some.Id, quota.Service);
        Assert.Equal(3L * 1024 * 1024, quota.Bytes);
        Assert.Equal(new[] { some.Layers[0].Id, some.Layers[1].Id }, quota.Layers);
    }

    public void Dispose()
    {
        _cache.Dispose();

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leaked temp directory is not worth failing a run over.
        }
    }

    /// <summary>Stores the tile, then lets two hours pass: an hour past its lifetime, well inside a day.</summary>
    private async Task ExpiredCopyAsync()
    {
        await _cache.WriteAsync(Key, Bytes, CancellationToken.None);
        _clock.Advance(TimeSpan.FromHours(2));
    }

    /// <summary><c>ServeTileAsync</c>'s per-layer step: the build, and the stand-in when it is refused.</summary>
    private Task<(VectorTileEndpoints.LayerPart Part, Exception? Refused)> PartAsync(Exception refusal, bool revalidate = false) =>
        VectorTileEndpoints.PartOrStandInAsync(
            () => BuildAsync(refusal, revalidate),
            () => VectorTileEndpoints.StaleOrNothingAsync(Key, Lifetime, Day, revalidate, _cache, CancellationToken.None));

    /// <summary>The build as serving, a seed and an export all run it, admitted by an admission that refuses.</summary>
    private Task<VectorTileEndpoints.LayerPart> BuildAsync(Exception refusal, bool revalidate = false) =>
        VectorTileEndpoints.CachedOrBuiltAsync(
            Key, Address, "layer", Lifetime, revalidate, _cache, new TileSingleFlight(), () => new NeverSource(),
            _ => ValueTask.FromException<IDisposable>(refusal),
            CancellationToken.None);

    private static async Task<DefaultHttpContext> RespondAsync(VectorTileEndpoints.LayerPart part, string? ifNoneMatch = null)
    {
        DefaultHttpContext context = NewContext();

        if (ifNoneMatch is not null)
        {
            context.Request.Headers.IfNoneMatch = ifNoneMatch;
        }

        await VectorTileEndpoints.RespondAsync(context, [(Layer(), part)], Lifetime, CancellationToken.None);
        return context;
    }

    private static DefaultHttpContext NewContext() => new() { Response = { Body = new MemoryStream() } };

    /// <summary>The weak tag <c>WriteTileAsync</c> computes from the bytes, written out so the test does not share its code.</summary>
    private static string ETagOf(byte[] bytes) =>
        "W/\"" + Convert.ToHexString(SHA256.HashData(bytes).AsSpan(0, 16)) + "\"";

    private static PublishedLayer Layer() =>
        new(
            Guid.NewGuid(),
            new LayerDefinition("roads", "public", "roads", "geom", 3857, "objectid", "objectid", isHosted: true),
            "datastore",
            "Host=db;Database=tiles",
            GeometryKind.Polygon,
            owner: null,
            SharingScope.Public,
            ServiceStatus.Started);

    /// <summary>A source the refused admission never lets anybody reach.</summary>
    private sealed class NeverSource : ITileSource
    {
        public Task<byte[]> BuildAsync(TileAddress address, string layerName, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A refused build reached its source.");
    }
}
