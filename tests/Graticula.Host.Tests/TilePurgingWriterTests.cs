using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Features;
using Graticula.Platform.Catalog;
using Graticula.Tiles;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// A kept edit empties its layer's cached tiles; an edit that changed nothing does not.
/// </summary>
/// <remarks>
/// Written 2026-09-15. ADR-069 said the tile cache is told to drop entries on an edit and nothing
/// told it: on the showcase a tile fetched before an <c>applyEdits</c> delete came back after it
/// with <c>X-Tile-Cache: HIT</c>, the same ETag and the same bytes.
/// </remarks>
public sealed class TilePurgingWriterTests
{
    private static readonly Guid LayerId = Guid.NewGuid();

    private static EditBatch Batch() => new([], [], [1]);

    [Fact]
    public async Task A_kept_edit_empties_the_layer()
    {
        RecordingCache cache = new();
        TilePurgingWriter writer = new(new FixedWriter(new([], [], [EditResult.Ok(1)], RolledBack: false)), cache, [LayerId]);

        await writer.ApplyAsync(Batch(), CancellationToken.None);

        Assert.Equal([LayerId], cache.Purged);
    }

    [Fact]
    public async Task A_rolled_back_batch_leaves_the_cache_alone()
    {
        RecordingCache cache = new();
        TilePurgingWriter writer = new(
            new FixedWriter(new([EditResult.Ok(4), EditResult.Failed(-1, "bad")], [], [], RolledBack: true)), cache, [LayerId]);

        await writer.ApplyAsync(Batch(), CancellationToken.None);

        Assert.Empty(cache.Purged);
    }

    [Fact]
    public async Task A_batch_in_which_every_edit_failed_leaves_the_cache_alone()
    {
        RecordingCache cache = new();
        TilePurgingWriter writer = new(
            new FixedWriter(new([], [EditResult.Failed(7, "no such feature")], [], RolledBack: false)), cache, [LayerId]);

        await writer.ApplyAsync(Batch(), CancellationToken.None);

        Assert.Empty(cache.Purged);
    }

    /// <remarks>
    /// <b>ADR-186: an edit empties the tiles it reached, where its feature was and where it is now.</b> A feature
    /// moved from near the origin to the far corner of the world reaches both places and nothing between.
    /// </remarks>
    [Fact]
    public async Task A_located_edit_empties_the_tiles_it_reached_and_keeps_the_rest()
    {
        RecordingCache cache = new();
        VectorTileScheme scheme = VectorTileScheme.WebMercator;
        TilePurgingWriter writer = new(
            new LocatingWriter(
                new([], [EditResult.Ok(1)], [], RolledBack: false),
                new Graticula.Geometries.Envelope(10, 10, 20, 20),
                new Graticula.Geometries.Envelope(20_000_000, 20_000_000, 20_000_010, 20_000_010)),
            cache, [LayerId], _ => Task.FromResult<VectorTileScheme?>(scheme));

        await writer.ApplyAsync(new([], [new FeatureUpdate(1, new Dictionary<string, object?>(), null)], []), CancellationToken.None);

        Assert.Empty(cache.Purged);
        (Guid layer, Func<TileAddress, bool> affected) = Assert.Single(cache.PurgedByAddress);
        Assert.Equal(LayerId, layer);

        Assert.True(affected(new TileAddress(10, 512, 511)));     // touches the origin
        Assert.True(affected(new TileAddress(10, 1023, 0)));      // the far corner
        Assert.True(affected(new TileAddress(0, 0, 0)));          // the whole world
        Assert.False(affected(new TileAddress(10, 200, 700)));    // neither
        Assert.False(affected(new TileAddress(10, 1023, 1023)));  // the other corner
    }

    [Fact]
    public async Task An_edit_whose_extent_cannot_be_read_empties_the_whole_layer()
    {
        RecordingCache cache = new();
        TilePurgingWriter writer = new(
            new LocatingWriter(new([], [], [EditResult.Ok(1)], RolledBack: false), null, null),
            cache, [LayerId], _ => Task.FromResult<VectorTileScheme?>(VectorTileScheme.WebMercator));

        await writer.ApplyAsync(Batch(), CancellationToken.None);

        Assert.Equal([LayerId], cache.Purged);
        Assert.Empty(cache.PurgedByAddress);
    }

    /// <remarks>A layer showing the same rows under another service (ADR-113) may be cut on another scheme.</remarks>
    [Fact]
    public async Task A_view_of_the_edited_rows_is_emptied_whole()
    {
        Guid view = Guid.NewGuid();
        RecordingCache cache = new();
        TilePurgingWriter writer = new(
            new LocatingWriter(
                new([], [], [EditResult.Ok(1)], RolledBack: false),
                new Graticula.Geometries.Envelope(0, 0, 1, 1),
                Graticula.Geometries.Envelope.Empty),
            cache, [LayerId, view], _ => Task.FromResult<VectorTileScheme?>(VectorTileScheme.WebMercator));

        await writer.ApplyAsync(Batch(), CancellationToken.None);

        Assert.Equal([view], cache.Purged);
        Assert.Equal(LayerId, Assert.Single(cache.PurgedByAddress).Layer);
    }

    [Fact]
    public void The_writer_both_editing_faces_get_empties_the_cache()
    {
        // The property the fix rests on: ArcGIS applyEdits and OGC API Features both take their
        // writer from WriterFor, so the decoration there is what reaches them. Nothing connects —
        // building a pool is lazy.
        using LayerConnections connections = new(
            new ConnectionBudget(0, 0), new SourceBreaker(), tiles: new RecordingCache());

        PublishedLayer layer = new(
            LayerId,
            new Graticula.Catalog.LayerDefinition("roads", "hosted", "roads", "geom", 3857, "objectid", "objectid", true),
            "source",
            "Host=localhost;Port=1;Database=nothing",
            Graticula.Geometries.GeometryKind.Polygon,
            null,
            Graticula.Platform.Identity.SharingScope.Private,
            ServiceStatus.Started);

        Assert.IsType<TilePurgingWriter>(connections.WriterFor(layer, []));
    }

    /// <summary>A store that answers where features are: <paramref name="before"/> first, <paramref name="after"/> next.</summary>
    private sealed class LocatingWriter(EditOutcome outcome, Graticula.Geometries.Envelope? before, Graticula.Geometries.Envelope? after)
        : IFeatureWriter, IEditedExtent
    {
        private int _asked;

        public Task<EditOutcome> ApplyAsync(EditBatch batch, CancellationToken cancellationToken) =>
            Task.FromResult(outcome);

        public Task<Graticula.Geometries.Envelope?> ExtentAsync(
            IReadOnlyCollection<long> identities, int srid, CancellationToken cancellationToken) =>
            Task.FromResult(_asked++ == 0 ? before : after);
    }

    private sealed class FixedWriter(EditOutcome outcome) : IFeatureWriter
    {
        public Task<EditOutcome> ApplyAsync(EditBatch batch, CancellationToken cancellationToken) =>
            Task.FromResult(outcome);
    }

    private sealed class RecordingCache : ITileCache
    {
        public List<Guid> Purged { get; } = [];

        public Task<CachedTile> ReadAsync(TileCacheKey key, TimeSpan lifetime, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CachedTile> ReadExpiredAsync(
            TileCacheKey key, TimeSpan lifetime, TimeSpan staleLimit, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task WriteAsync(TileCacheKey key, byte[] tile, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task WriteAsync(TileCacheKey key, byte[] tile, TileCacheQuota? quota, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public List<(Guid Layer, Func<TileAddress, bool> Affected)> PurgedByAddress { get; } = [];

        public int Purge(Guid layerId)
        {
            Purged.Add(layerId);
            return 0;
        }

        public int Purge(Guid layerId, Func<TileAddress, bool> affected)
        {
            PurgedByAddress.Add((layerId, affected));
            return 0;
        }

        public (int Entries, long Bytes) Report(Guid? layerId) => (0, 0);
    }
}
