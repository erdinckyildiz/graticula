using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Graticula.Tiles;

/// <summary>
/// What makes two tile requests the same tile.
/// </summary>
/// <param name="LayerId">The published layer.</param>
/// <param name="Fingerprint">
/// A hash of everything about the layer that changes the bytes.
/// </param>
/// <param name="Address">Which tile.</param>
/// <remarks>
/// <para>
/// <b>The fingerprint is what makes invalidation structural</b> — ADR-010 §4.
/// When a table gains, loses or retypes a column, the fingerprint changes, and
/// every key derived from the old one becomes unreachable. There is no sweep to
/// run and no entries to hunt down: the old bytes become garbage to collect
/// rather than stale data to find. That is the difference between an
/// invalidation somebody has to remember to trigger and one that cannot be
/// forgotten.
/// </para>
/// <para>
/// <b>The principal is deliberately not in the key.</b> ADR-010 §4's rule is
/// that an entry may be shared by any two requests that would produce
/// byte-identical output under their own authorization. For tiles the
/// authorization is uniform — a layer is readable or it is not — so the check
/// happens <em>before</em> the lookup and all authorized callers share one
/// entry. Putting the principal in the key would be correct and catastrophic:
/// every user would get their own copy of the pyramid.
/// </para>
/// </remarks>
public readonly record struct TileCacheKey(Guid LayerId, string Fingerprint, TileAddress Address)
{
    /// <summary>
    /// Builds a fingerprint from the things that change a tile's bytes.
    /// </summary>
    /// <param name="srid">The layer's spatial reference.</param>
    /// <param name="geometryColumn">Which column is drawn.</param>
    /// <param name="attributes">Which columns ride along as tags, in order.</param>
    /// <param name="extent">The MVT coordinate space.</param>
    /// <param name="buffer">The margin, in tile units.</param>
    /// <returns>Eight hex characters.</returns>
    /// <remarks>
    /// <para>
    /// <b>Everything here changes the bytes; nothing else is included.</b> The
    /// layer's <em>name</em> is not, because renaming a service does not change
    /// a single byte of any tile in it — including it would throw away a whole
    /// pyramid for a label. The sharing scope is not, because it decides whether
    /// the caller reaches the cache at all rather than what they get.
    /// </para>
    /// <para>
    /// <b>Eight characters, and that is a deliberate collision budget.</b> 32
    /// bits across the handful of distinct shapes one layer has in its lifetime
    /// is not a risk worth more path length; a collision would serve a tile
    /// built from a different column list, so the cost is real but the
    /// probability is a birthday problem over single digits.
    /// </para>
    /// </remarks>
    public static string FingerprintOf(
        int srid,
        string geometryColumn,
        System.Collections.Generic.IEnumerable<string> attributes,
        int extent,
        int buffer)
    {
        ArgumentNullException.ThrowIfNull(attributes);

        StringBuilder canonical = new();
        canonical.Append(CultureInfo.InvariantCulture, $"srid={srid};geom={geometryColumn};")
                 .Append(CultureInfo.InvariantCulture, $"extent={extent};buffer={buffer};tags=");

        // Order matters and is preserved rather than sorted: the attribute order
        // is the order the columns reach ST_AsMVT, and the endpoint derives it
        // from the table. Two different orders can produce different tag tables,
        // so treating them as the same shape would be wrong.
        foreach (string attribute in attributes)
        {
            canonical.Append(attribute).Append(',');
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        return Convert.ToHexStringLower(hash.AsSpan(0, 4));
    }

    /// <summary>
    /// The storage path for this key, relative to the cache root.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Derivable from the key alone, which is failure-scenario N2</b>
    /// (ADR-010 §3). A lookup that needed an index row would turn a platform
    /// store outage into a total cache miss — at exactly the moment the data
    /// source may also be unreachable, so the cache would stop working precisely
    /// when it is the only thing that could still answer.
    /// </para>
    /// <para>
    /// <b>Sharded by zoom and column.</b> A single flat directory holding a
    /// seeded pyramid is millions of entries, and most filesystems degrade
    /// badly. The z/x split gives at most 4096 children per directory at the
    /// zooms that matter.
    /// </para>
    /// <para>
    /// <b>And the pipeline's generation sits under the layer, not above it —
    /// <see cref="TilePipeline"/>, D-155.</b> The fingerprint below describes the layer
    /// and nothing described the code, so an upgrade that changed how a tile is drawn
    /// served the old bytes until each entry expired. The segment makes a pipeline change
    /// structurally invalidating in the same way a schema change already was: old entries
    /// become unreachable rather than stale, and there is no sweep to remember.
    /// </para>
    /// <para>
    /// <b>Second, not first, and that ordering is load-bearing.</b>
    /// <c>FileSystemTileCache.Purge</c> matches index keys by the layer id as a prefix
    /// and deletes <c>{root}/{layerId}</c> as a directory. A version segment in front of
    /// the id would make every purge match nothing and delete nothing, silently — an
    /// unpublish would leave its tiles behind and republishing the name over a different
    /// table would serve them.
    /// </para>
    /// </remarks>
    public string Path() => string.Create(
        CultureInfo.InvariantCulture,
        $"{LayerId:N}/v{TilePipeline.Version}/{Fingerprint}/{Address.Z}/{Address.X}/{Address.Y}.mvt");
}
