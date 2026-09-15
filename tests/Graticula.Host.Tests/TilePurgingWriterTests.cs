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
        TilePurgingWriter writer = new(new FixedWriter(new([], [], [EditResult.Ok(1)], RolledBack: false)), cache, LayerId);

        await writer.ApplyAsync(Batch(), CancellationToken.None);

        Assert.Equal([LayerId], cache.Purged);
    }

    [Fact]
    public async Task A_rolled_back_batch_leaves_the_cache_alone()
    {
        RecordingCache cache = new();
        TilePurgingWriter writer = new(
            new FixedWriter(new([EditResult.Ok(4), EditResult.Failed(-1, "bad")], [], [], RolledBack: true)), cache, LayerId);

        await writer.ApplyAsync(Batch(), CancellationToken.None);

        Assert.Empty(cache.Purged);
    }

    [Fact]
    public async Task A_batch_in_which_every_edit_failed_leaves_the_cache_alone()
    {
        RecordingCache cache = new();
        TilePurgingWriter writer = new(
            new FixedWriter(new([], [EditResult.Failed(7, "no such feature")], [], RolledBack: false)), cache, LayerId);

        await writer.ApplyAsync(Batch(), CancellationToken.None);

        Assert.Empty(cache.Purged);
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

        public Task WriteAsync(TileCacheKey key, byte[] tile, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public int Purge(Guid layerId)
        {
            Purged.Add(layerId);
            return 0;
        }

        public (int Entries, long Bytes) Report(Guid? layerId) => (0, 0);
    }
}
