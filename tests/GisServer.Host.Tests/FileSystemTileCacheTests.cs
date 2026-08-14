using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GisServer.Tiles;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace GisServer.Host.Tests;

/// <summary>
/// The tile cache: what it holds, what it forgets, and what it refuses to break.
/// </summary>
/// <remarks>
/// <para>
/// Against a real temporary directory rather than an abstraction. Most of what
/// can go wrong here is filesystem behaviour — a half-written file, a directory
/// that will not delete, a budget counted from zero after a restart — and none
/// of it appears against an in-memory fake.
/// </para>
/// <para>
/// The tests that matter most are the ones about failing soft. A cache is an
/// optimisation, and ADR-010 §3's rule is that an optimisation which can fail a
/// request is a liability.
/// </para>
/// </remarks>
public sealed class FileSystemTileCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "gis-tilecache-" + Guid.NewGuid().ToString("N"));

    private readonly FakeTimeProvider _clock = new();
    private bool _disposed;

    private static readonly Guid Layer = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();

    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(60);

    private FileSystemTileCache Build(long budget = 1_000_000, long perLayer = 1_000_000) =>
        new(_root, budget, perLayer, Lifetime, _clock, NullLoggerFactory.Instance);

    private static TileCacheKey Key(int z = 5, int x = 1, int y = 2, Guid? layer = null) =>
        new(layer ?? Layer, "abcd1234", new TileAddress(z, x, y));

    private static byte[] Tile(int size = 64) => Encoding.UTF8.GetBytes(new string('t', size));

    // ---------- the basics ----------

    [Fact]
    public async Task A_stored_tile_comes_back_byte_for_byte()
    {
        using FileSystemTileCache cache = Build();
        byte[] tile = Tile();

        await cache.WriteAsync(Key(), tile, CancellationToken.None);
        CachedTile found = await cache.ReadAsync(Key(), CancellationToken.None);

        Assert.Equal(TileCacheOutcome.Hit, found.Outcome);
        Assert.Equal(tile, found.Bytes);
    }

    [Fact]
    public async Task Nothing_stored_is_a_miss()
    {
        using FileSystemTileCache cache = Build();

        Assert.Equal(
            TileCacheOutcome.Miss,
            (await cache.ReadAsync(Key(), CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task An_empty_tile_is_remembered_as_empty_rather_than_as_nothing()
    {
        // ADR-010 §2's negative caching, and it is not a nicety: most of a
        // sparse layer's pyramid is emptiness, so collapsing Empty into Miss
        // makes the cache useless for the tiles it holds most of.
        using FileSystemTileCache cache = Build();

        await cache.WriteAsync(Key(), [], CancellationToken.None);
        CachedTile found = await cache.ReadAsync(Key(), CancellationToken.None);

        Assert.Equal(TileCacheOutcome.Empty, found.Outcome);
        Assert.True(found.Answered, "an empty tile is an answer, so the caller must not rebuild it");
    }

    [Fact]
    public async Task Two_tiles_of_the_same_layer_do_not_collide()
    {
        using FileSystemTileCache cache = Build();

        await cache.WriteAsync(Key(x: 1), Tile(10), CancellationToken.None);
        await cache.WriteAsync(Key(x: 2), Tile(20), CancellationToken.None);

        Assert.Equal(10, (await cache.ReadAsync(Key(x: 1), CancellationToken.None)).Bytes.Length);
        Assert.Equal(20, (await cache.ReadAsync(Key(x: 2), CancellationToken.None)).Bytes.Length);
    }

    // ---------- expiry ----------

    [Fact]
    public async Task An_entry_past_its_lifetime_is_a_miss()
    {
        using FileSystemTileCache cache = Build();

        await cache.WriteAsync(Key(), Tile(), CancellationToken.None);
        _clock.Advance(Lifetime + TimeSpan.FromSeconds(1));

        Assert.Equal(
            TileCacheOutcome.Miss,
            (await cache.ReadAsync(Key(), CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task An_entry_one_tick_before_its_lifetime_is_still_a_hit()
    {
        using FileSystemTileCache cache = Build();

        await cache.WriteAsync(Key(), Tile(), CancellationToken.None);
        _clock.Advance(Lifetime - TimeSpan.FromSeconds(1));

        Assert.Equal(
            TileCacheOutcome.Hit,
            (await cache.ReadAsync(Key(), CancellationToken.None)).Outcome);
    }

    // ---------- the fingerprint ----------

    [Fact]
    public async Task A_changed_shape_makes_the_old_entries_unreachable_with_no_sweep()
    {
        // ADR-010 §4's structural invalidation, and the reason the fingerprint
        // is in the key rather than tracked somewhere. Nothing has to remember
        // to invalidate: the old bytes simply become garbage.
        using FileSystemTileCache cache = Build();

        TileCacheKey before = new(Layer, "aaaa1111", new TileAddress(5, 1, 2));
        TileCacheKey after = new(Layer, "bbbb2222", new TileAddress(5, 1, 2));

        await cache.WriteAsync(before, Tile(), CancellationToken.None);

        Assert.Equal(
            TileCacheOutcome.Miss,
            (await cache.ReadAsync(after, CancellationToken.None)).Outcome);
    }

    [Fact]
    public void A_fingerprint_changes_when_anything_that_changes_the_bytes_changes()
    {
        string baseline = TileCacheKey.FingerprintOf(3857, "geom", ["a", "b"], 4096, 64);

        Assert.NotEqual(baseline, TileCacheKey.FingerprintOf(4326, "geom", ["a", "b"], 4096, 64));
        Assert.NotEqual(baseline, TileCacheKey.FingerprintOf(3857, "way", ["a", "b"], 4096, 64));
        Assert.NotEqual(baseline, TileCacheKey.FingerprintOf(3857, "geom", ["a"], 4096, 64));
        Assert.NotEqual(baseline, TileCacheKey.FingerprintOf(3857, "geom", ["a", "b"], 512, 64));
        Assert.NotEqual(baseline, TileCacheKey.FingerprintOf(3857, "geom", ["a", "b"], 4096, 8));

        // Order matters: the columns reach ST_AsMVT in this order and a
        // different order can produce a different tag table.
        Assert.NotEqual(baseline, TileCacheKey.FingerprintOf(3857, "geom", ["b", "a"], 4096, 64));
    }

    [Fact]
    public void The_same_shape_produces_the_same_fingerprint_every_time()
    {
        // Otherwise every restart invalidates the whole cache, which would look
        // like the cache simply not working.
        Assert.Equal(
            TileCacheKey.FingerprintOf(3857, "geom", ["a", "b"], 4096, 64),
            TileCacheKey.FingerprintOf(3857, "geom", ["a", "b"], 4096, 64));
    }

    // ---------- purging ----------

    [Fact]
    public async Task Purging_a_layer_removes_its_tiles_and_leaves_the_others()
    {
        using FileSystemTileCache cache = Build();

        await cache.WriteAsync(Key(x: 1), Tile(), CancellationToken.None);
        await cache.WriteAsync(Key(x: 2), Tile(), CancellationToken.None);
        await cache.WriteAsync(Key(layer: Other), Tile(), CancellationToken.None);

        Assert.Equal(2, cache.Purge(Layer));

        Assert.Equal(
            TileCacheOutcome.Miss,
            (await cache.ReadAsync(Key(x: 1), CancellationToken.None)).Outcome);
        Assert.Equal(
            TileCacheOutcome.Hit,
            (await cache.ReadAsync(Key(layer: Other), CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Purging_frees_the_budget_it_was_using()
    {
        using FileSystemTileCache cache = Build();

        await cache.WriteAsync(Key(), Tile(500), CancellationToken.None);
        Assert.Equal(500, cache.Report(Layer).Bytes);

        cache.Purge(Layer);

        Assert.Equal((0, 0L), cache.Report(Layer));
        Assert.Equal((0, 0L), cache.Report(null));
    }

    [Fact]
    public void Purging_a_layer_that_has_nothing_cached_is_not_an_error()
    {
        using FileSystemTileCache cache = Build();

        Assert.Equal(0, cache.Purge(Guid.NewGuid()));
    }

    // ---------- the budget ----------

    [Fact]
    public async Task The_cache_evicts_rather_than_growing_past_its_budget()
    {
        // failure-scenarios N6. Without this the cache fills any disk given
        // time, and "the GIS server filled the disk" is a memorable first
        // incident.
        using FileSystemTileCache cache = Build(budget: 1000, perLayer: 1000);

        for (int i = 0; i < 40; i++)
        {
            await cache.WriteAsync(Key(x: i), Tile(100), CancellationToken.None);
        }

        Assert.True(
            cache.Report(null).Bytes <= 1000,
            $"the cache holds {cache.Report(null).Bytes} bytes against a 1000-byte budget");
    }

    [Fact]
    public async Task Eviction_takes_the_least_recently_used_and_keeps_what_is_being_read()
    {
        using FileSystemTileCache cache = Build(budget: 500, perLayer: 500);

        await cache.WriteAsync(Key(x: 1), Tile(100), CancellationToken.None);
        await cache.WriteAsync(Key(x: 2), Tile(100), CancellationToken.None);

        // Keep reading the first one, so it is the most recently used and the
        // second is the coldest thing in the cache.
        for (int i = 0; i < 6; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            await cache.ReadAsync(Key(x: 1), CancellationToken.None);
            await cache.WriteAsync(Key(x: 10 + i), Tile(100), CancellationToken.None);
        }

        Assert.Equal(
            TileCacheOutcome.Hit,
            (await cache.ReadAsync(Key(x: 1), CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task A_tile_bigger_than_a_layer_may_hold_is_refused_rather_than_emptying_the_cache()
    {
        // It would evict everything else to make room and then still not fit.
        using FileSystemTileCache cache = Build(budget: 1000, perLayer: 200);

        await cache.WriteAsync(Key(x: 1), Tile(50), CancellationToken.None);
        await cache.WriteAsync(Key(x: 2), Tile(500), CancellationToken.None);

        Assert.Equal(
            TileCacheOutcome.Hit,
            (await cache.ReadAsync(Key(x: 1), CancellationToken.None)).Outcome);
        Assert.Equal(
            TileCacheOutcome.Miss,
            (await cache.ReadAsync(Key(x: 2), CancellationToken.None)).Outcome);
    }

    // ---------- surviving a restart ----------

    [Fact]
    public async Task A_new_instance_adopts_what_the_last_one_left()
    {
        // <b>The budget hole that the budget alone does not close.</b> Without
        // adoption the files stay, reads keep hitting them, and the byte count
        // starts from zero — so the cache grows without limit across restarts
        // while reporting itself as under control.
        using (FileSystemTileCache first = Build())
        {
            await first.WriteAsync(Key(x: 1), Tile(300), CancellationToken.None);
            await first.WriteAsync(Key(x: 2), Tile(200), CancellationToken.None);
        }

        using FileSystemTileCache second = Build();

        Assert.Equal((2, 500L), second.Report(null));
        Assert.Equal(
            TileCacheOutcome.Hit,
            (await second.ReadAsync(Key(x: 1), CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task An_adopted_entry_still_expires_on_its_own_age()
    {
        // Expiry is read from the file rather than from when it was adopted. If
        // adoption reset the clock, restarting the server would silently make
        // every stale tile fresh again.
        using (FileSystemTileCache first = Build())
        {
            await first.WriteAsync(Key(), Tile(), CancellationToken.None);
        }

        _clock.Advance(Lifetime + TimeSpan.FromMinutes(1));

        using FileSystemTileCache second = Build();

        Assert.Equal(
            TileCacheOutcome.Miss,
            (await second.ReadAsync(Key(), CancellationToken.None)).Outcome);
    }

    // ---------- failing soft ----------

    [Fact]
    public async Task A_cache_that_cannot_be_written_does_not_fail_the_caller()
    {
        // The rule that matters: a cache is an optimisation and an optimisation
        // that can fail a request is a liability. A file where a directory
        // should be makes every write throw underneath.
        string blocked = Path.Combine(_root, Layer.ToString("N"));
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(blocked, "not a directory");

        using FileSystemTileCache cache = Build();

        await cache.WriteAsync(Key(), Tile(), CancellationToken.None);

        Assert.Equal(
            TileCacheOutcome.Miss,
            (await cache.ReadAsync(Key(), CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task An_unreadable_root_leaves_a_usable_cache_that_simply_never_hits()
    {
        using FileSystemTileCache cache = new(
            Path.Combine(_root, "deep", "er"), 1000, 1000, Lifetime, _clock,
            NullLoggerFactory.Instance);

        await cache.WriteAsync(Key(), Tile(), CancellationToken.None);

        Assert.Equal(
            TileCacheOutcome.Hit,
            (await cache.ReadAsync(Key(), CancellationToken.None)).Outcome);
    }

    // ---------- reporting ----------

    [Fact]
    public async Task The_report_separates_one_layer_from_the_whole_cache()
    {
        // ADR-010 §6b. An operator asking "is this seeded" or "why is the disk
        // full" has no other way to find out.
        using FileSystemTileCache cache = Build();

        await cache.WriteAsync(Key(x: 1), Tile(100), CancellationToken.None);
        await cache.WriteAsync(Key(x: 2), Tile(100), CancellationToken.None);
        await cache.WriteAsync(Key(layer: Other), Tile(300), CancellationToken.None);

        Assert.Equal((2, 200L), cache.Report(Layer));
        Assert.Equal((1, 300L), cache.Report(Other));
        Assert.Equal((3, 500L), cache.Report(null));
    }

    [Fact]
    public async Task Rewriting_a_tile_does_not_double_count_it()
    {
        using FileSystemTileCache cache = Build();

        await cache.WriteAsync(Key(), Tile(100), CancellationToken.None);
        await cache.WriteAsync(Key(), Tile(250), CancellationToken.None);

        Assert.Equal((1, 250L), cache.Report(null));
    }

    // ---------- the path ----------

    [Fact]
    public void A_key_maps_to_a_path_nothing_else_needs_to_know()
    {
        // failure-scenario N2: lookup must not need an index row, or a platform
        // store outage turns every request into a miss at exactly the moment the
        // source may also be down.
        Guid layer = Guid.Parse("0f9c9f61-0f27-4a51-9e26-1e1a1b2c3d4e");
        string path = new TileCacheKey(layer, "abcd1234", new TileAddress(7, 65, 42)).Path();

        Assert.Equal("0f9c9f610f274a519e261e1a1b2c3d4e/abcd1234/7/65/42.mvt", path);
    }

    [Fact]
    public async Task No_temporary_files_are_left_behind()
    {
        // A half-written tile read by somebody else decodes to fewer features
        // rather than to an error, which is a wrong map with nothing to say so.
        using FileSystemTileCache cache = Build();

        for (int i = 0; i < 10; i++)
        {
            await cache.WriteAsync(Key(x: i), Tile(), CancellationToken.None);
        }

        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp", SearchOption.AllDirectories));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leaked temp directory is not worth failing a test run over.
        }
    }
}
