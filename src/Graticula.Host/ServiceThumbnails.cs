using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;

namespace Graticula.Host;

/// <summary>
/// The rendered pictures the console's lists show, drawn once and held.
/// </summary>
/// <remarks>
/// <para>
/// <b>The word this exists for is <em>cached</em> — [D-58](../../docs/architecture-debt.md).</b>
/// That row's trigger is *a real thumbnail, cached, drawn once rather than per viewer*, and the
/// measurement behind it is why the middle clause matters: the sampled canvas it replaces cost
/// 17–23 ms and 139.5 kB per viewer per visit, and the render costs 70–76 ms and 1.8 kB. The
/// render is slower per request and seventy-seven times smaller on the wire — so a list of
/// forty is 5.6 MB against 72 kB — but only if the 70 ms is paid once. Without this class the
/// swap trades a cheap request every viewer makes for an expensive one every viewer makes.
/// </para>
/// <para>
/// <b>In memory, bounded by count, and that is the whole design.</b> A thumbnail is a couple of
/// kilobytes; the ceiling here is a few hundred of them, which is smaller than one tile. Putting
/// them on disk or in the catalogue would buy survival across a restart and cost an
/// invalidation story, a migration and a path to configure — <see href="../../CLAUDE.md">§6</see>'s
/// question, *what concrete problem does this solve*, has no answer for that yet. A restart
/// costs one render per service the next time somebody opens the list.
/// </para>
/// <para>
/// <b>Time is the invalidation, deliberately, and it is not free of consequence.</b> Nothing
/// tells this class that somebody edited a layer's symbology or added ten thousand features, so
/// a changed service shows its old picture until the entry ages out. The alternative is a hook
/// on every write path that can change what a map looks like, which is a list nobody can keep
/// complete — and a stale thumbnail for a few minutes is a much smaller wrong than a
/// half-maintained invalidation that is wrong without a bound.
/// </para>
/// <para>
/// <b>Node-local.</b> Two servers behind a load balancer hold their own, render their own, and
/// agree because they are drawing the same data. Nothing here is shared state.
/// </para>
/// </remarks>
public sealed class ServiceThumbnails
{
    /// <summary>How long a picture is believed.</summary>
    /// <remarks>
    /// <b>Five minutes, and the number is a judgement rather than a measurement.</b> Long
    /// enough that opening the list, following a service and coming back costs one render;
    /// short enough that somebody who changes a symbology and reloads twice sees it.
    /// </remarks>
    public static readonly TimeSpan Age = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How many pictures are held.
    /// </summary>
    /// <remarks>
    /// <b>256 at roughly two kilobytes each is half a megabyte</b>, and the scale target is
    /// 100–1,000 services (<see href="../../CLAUDE.md">§7</see>), so a deployment at the top
    /// of that range holds a quarter of its services and re-renders the rest. That is the right
    /// shape: the cost of a miss is 70 ms, not a failure.
    /// </remarks>
    public const int Capacity = 256;

    private readonly ConcurrentDictionary<string, Held> _held = new(StringComparer.Ordinal);

    /// <summary>One picture and what a browser needs to revalidate it.</summary>
    /// <param name="Bytes">The PNG.</param>
    /// <param name="ETag">Its entity tag, quoted and strong.</param>
    /// <param name="Drawn">When it was rendered.</param>
    public sealed record Held(byte[] Bytes, string ETag, DateTimeOffset Drawn);

    /// <summary>The key one picture is held under.</summary>
    /// <param name="service">The qualified service name.</param>
    /// <param name="layer">Which layer is drawn.</param>
    /// <param name="width">Pixels across.</param>
    /// <param name="height">Pixels down.</param>
    /// <returns>The key.</returns>
    public static string KeyFor(string service, int layer, int width, int height) =>
        string.Create(
            CultureInfo.InvariantCulture, $"{service}|{layer}|{width}x{height}");

    /// <summary>The picture, if one is held and still believed.</summary>
    /// <param name="key">From <see cref="KeyFor"/>.</param>
    /// <param name="now">The clock, passed in so a test does not wait five minutes.</param>
    /// <returns>The picture, or null.</returns>
    public Held? Find(string key, DateTimeOffset now)
    {
        if (!_held.TryGetValue(key, out Held? found))
        {
            return null;
        }

        if (now - found.Drawn <= Age)
        {
            return found;
        }

        // Expired. Removed on the way past rather than by a timer: a picture nobody asks for
        // again costs nothing to keep until the capacity sweep reaches it.
        _held.TryRemove(new KeyValuePair<string, Held>(key, found));

        return null;
    }

    /// <summary>Holds a picture, evicting the oldest if the store is full.</summary>
    /// <param name="key">From <see cref="KeyFor"/>.</param>
    /// <param name="bytes">The PNG.</param>
    /// <param name="now">The clock.</param>
    /// <returns>What was stored, so the caller can answer from it.</returns>
    public Held Keep(string key, byte[] bytes, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        Held held = new(bytes, Tag(bytes), now);

        _held[key] = held;

        // <b>Oldest-drawn rather than least-recently-used, and the difference is deliberate.</b>
        // Tracking reads would mean writing to the dictionary on every hit, which is the path
        // this exists to make cheap. Every entry expires in five minutes anyway, so the two
        // orders differ only under pressure and only for entries about to expire.
        while (_held.Count > Capacity)
        {
            KeyValuePair<string, Held> oldest = _held
                .OrderBy(e => e.Value.Drawn)
                .First();

            _held.TryRemove(oldest);
        }

        return held;
    }

    /// <summary>Forgets everything, for a test or an operator.</summary>
    public void Forget() => _held.Clear();

    /// <summary>A strong entity tag for these bytes.</summary>
    /// <remarks>
    /// <b>From the bytes rather than from the clock.</b> A tag that changed every render would
    /// make a browser re-download an identical picture every five minutes; a hash means a
    /// service whose map has not changed revalidates with a 304 and no body.
    /// </remarks>
    private static string Tag(byte[] bytes) =>
        "\"" + Convert.ToHexString(SHA256.HashData(bytes).AsSpan(0, 8)).ToLowerInvariant() + "\"";
}
