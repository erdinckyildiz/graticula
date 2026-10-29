using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Graticula.Tiles;
using Microsoft.Extensions.Logging;

namespace Graticula.Host;

/// <summary>
/// Notes a layer drawn into a tile with more features than its visible range is measured for — D-267.
/// </summary>
/// <remarks>
/// <para>
/// <b>Suggest, never tighten — owner decision 2026-10-29.</b> A range is measured once, at publish (ADR-070), and a
/// layer whose data grows past it goes on building the tiles the range exists to prevent. The server sees that the
/// moment it builds such a tile, and that is when it says so: the note goes on the layer, its page shows it beside
/// the range with the measurement one press away, and the range itself is left exactly as it is.
/// </para>
/// <para>
/// <b>Counted from the bytes just built, so it costs nothing on a cached tile and nothing on a small one.</b> A part
/// under <see cref="SmallestDense"/> cannot hold the features this looks for and is not read at all; one above it is
/// walked as protobuf, which is a scan of a buffer already in memory beside a build that took the database seconds.
/// </para>
/// <para>
/// <b>Written at most a few times per layer.</b> This node remembers the densest tile it noted per layer and the
/// statement stores a note only over a sparser one, so a zoomed-out map of a dense layer writes once, not once per
/// tile. A note that cannot be written is logged and the tile served: a suggestion is never worth a failed map.
/// </para>
/// </remarks>
internal sealed partial class DenseTileNotices
{
    /// <summary>How many layers are remembered; the catalogue bounds it, this bounds a runaway one.</summary>
    public const int Ceiling = 4096;

    /// <summary>
    /// The smallest part read: an encoded feature is at least a tag, an id and a two-integer geometry, about eight
    /// bytes, so fewer than eight bytes per feature over the measure cannot be a dense tile.
    /// </summary>
    public const int SmallestDense = VisibleRangeSuggestion.FeaturesPerTile * 8;

    private readonly Func<Guid, DenseTile, CancellationToken, Task<bool>> _note;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<Guid, int> _noted = new();

    /// <summary>Notes into the catalogue.</summary>
    /// <param name="catalog">Where the note is kept.</param>
    /// <param name="log">Where a stored note and a failed one are said.</param>
    public DenseTileNotices(IAdminCatalog catalog, ILogger<DenseTileNotices> log)
        : this(catalog.NoteDenseTileAsync, log)
    {
    }

    /// <summary>Notes through a function — the tests'.</summary>
    /// <param name="note">Stores a note and says whether it was stored.</param>
    /// <param name="log">Where a stored note and a failed one are said.</param>
    internal DenseTileNotices(Func<Guid, DenseTile, CancellationToken, Task<bool>> note, ILogger log)
    {
        _note = note;
        _log = log;
    }

    /// <summary>Looks at a part just built and notes it when it holds more features than a range is measured for.</summary>
    /// <param name="layerId">The layer.</param>
    /// <param name="layerName">Its name, for the log.</param>
    /// <param name="address">The tile.</param>
    /// <param name="part">The part's bytes, as encoded.</param>
    /// <param name="cancellation">The caller's.</param>
    /// <returns>Whether a note was stored.</returns>
    public async Task<bool> LookAsync(
        Guid layerId, string layerName, TileAddress address, byte[] part, CancellationToken cancellation)
    {
        if (part is null || part.Length < SmallestDense)
        {
            return false;
        }

        int features = CountFeatures(part);

        if (features <= VisibleRangeSuggestion.FeaturesPerTile)
        {
            return false;
        }

        if (_noted.TryGetValue(layerId, out int seen) && seen >= features)
        {
            return false;
        }

        if (_noted.Count >= Ceiling && !_noted.ContainsKey(layerId))
        {
            return false;
        }

        _noted[layerId] = features;

        try
        {
            bool stored = await _note(
                    layerId,
                    new DenseTile(address.Z, address.X, address.Y, features, DateTimeOffset.UtcNow),
                    cancellation)
                .ConfigureAwait(false);

            if (stored)
            {
                Noted(_log, layerName, features, address.Z, address.X, address.Y);
            }

            return stored;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            NotNoted(_log, layerName, e.Message);
            return false;
        }
    }

    /// <summary>Forgets what this node noted for a layer, so the next dense tile is noted again — its range was set or kept.</summary>
    /// <param name="layerId">The layer.</param>
    public void Forget(Guid layerId) => _noted.TryRemove(layerId, out _);

    /// <summary>
    /// How many features an encoded vector tile holds, across its layers — field 3 of the tile, field 2 of each
    /// layer, in the Mapbox Vector Tile specification's protobuf. A malformed buffer counts what it read before the fault.
    /// </summary>
    /// <param name="tile">The encoded tile or one layer's part of it.</param>
    /// <returns>The count.</returns>
    internal static int CountFeatures(ReadOnlySpan<byte> tile)
    {
        int count = 0;
        int at = 0;

        while (at < tile.Length)
        {
            if (!TryVarint(tile, ref at, out ulong key))
            {
                break;
            }

            int field = (int)(key >> 3);
            int wire = (int)(key & 7);

            if (wire == 2)
            {
                if (!TryVarint(tile, ref at, out ulong length) || length > (ulong)(tile.Length - at))
                {
                    break;
                }

                if (field == 3)
                {
                    count += FeaturesInLayer(tile.Slice(at, (int)length));
                }

                at += (int)length;
            }
            else if (!Skip(tile, ref at, wire))
            {
                break;
            }
        }

        return count;
    }

    private static int FeaturesInLayer(ReadOnlySpan<byte> layer)
    {
        int count = 0;
        int at = 0;

        while (at < layer.Length)
        {
            if (!TryVarint(layer, ref at, out ulong key))
            {
                break;
            }

            int wire = (int)(key & 7);

            if (wire == 2)
            {
                if (!TryVarint(layer, ref at, out ulong length) || length > (ulong)(layer.Length - at))
                {
                    break;
                }

                if (key >> 3 == 2)
                {
                    count++;
                }

                at += (int)length;
            }
            else if (!Skip(layer, ref at, wire))
            {
                break;
            }
        }

        return count;
    }

    private static bool Skip(ReadOnlySpan<byte> buffer, ref int at, int wire)
    {
        switch (wire)
        {
            case 0:
                return TryVarint(buffer, ref at, out _);
            case 1:
                at += 8;
                return at <= buffer.Length;
            case 5:
                at += 4;
                return at <= buffer.Length;
            default:
                return false;
        }
    }

    private static bool TryVarint(ReadOnlySpan<byte> buffer, ref int at, out ulong value)
    {
        value = 0;

        for (int shift = 0; shift < 64 && at < buffer.Length; shift += 7)
        {
            byte b = buffer[at++];
            value |= (ulong)(b & 0x7F) << shift;

            if ((b & 0x80) == 0)
            {
                return true;
            }
        }

        return false;
    }

    [LoggerMessage(EventId = 2366, Level = LogLevel.Information,
        Message = "Layer {Layer} was drawn into tile {Z}/{X}/{Y} with {Features} features, more than its visible range is measured for; its page suggests measuring the range again (D-267).")]
    private static partial void Noted(ILogger logger, string layer, int features, int z, int x, int y);

    [LoggerMessage(EventId = 2367, Level = LogLevel.Warning,
        Message = "Layer {Layer} was drawn into a dense tile and the note could not be kept: {Reason}")]
    private static partial void NotNoted(ILogger logger, string layer, string reason);
}
