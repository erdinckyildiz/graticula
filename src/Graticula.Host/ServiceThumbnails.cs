using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Host;

/// <summary>
/// The rendered pictures the console's lists show, drawn once and kept — [ADR-071](../../docs/adr/ADR-071-thumbnails-are-kept-until-redrawn.md).
/// </summary>
/// <remarks>
/// <para>
/// <b>The word this exists for is <em>cached</em> — [D-58](../../docs/architecture-debt.md).</b>
/// That row's trigger is *a real thumbnail, cached, drawn once rather than per viewer*: the sampled
/// canvas it replaced cost 17–23 ms and 139.5 kB per viewer per visit, and the render costs 70–76 ms
/// and 1.8 kB — seventy-seven times smaller on the wire, but only if the render is paid once.
/// </para>
/// <para>
/// <b>Kept on disk, and until somebody asks for a new one — owner, 2026-09-14:</b> <i>"her seferinde
/// thumbnail oluşturmak maliyetli. tek sefer oluşturup, gerekirse içeriye bir düğme koymak mantıklı."</i>
/// Until then pictures lived in memory for five minutes, and this remark answered §6's question —
/// <i>what concrete problem does disk solve</i> — with <i>none yet</i>. Two things gave it one: a
/// picture of a city's buildings reads the layer's record ceiling of features twice, which is seconds
/// rather than 70 ms, and the showcase restarts on every release, so every list was redrawn after each
/// one and again every five minutes after that. A picture is drawn the first time it is asked for, kept
/// under the server's state directory, and drawn again only when an administrator presses
/// <i>Redraw thumbnail</i> or the layer's symbology changes.
/// </para>
/// <para>
/// <b>Keyed by the layer's id, not its name.</b> A republished layer is a new id and gets a new picture
/// without anybody forgetting the old one; a renamed service keeps its picture. A deleted layer leaves
/// its file behind — a few kilobytes that nothing can ask for again, accepted rather than hooked into
/// every path that removes a layer.
/// </para>
/// <para>
/// <b>Node-local.</b> Two servers behind a load balancer keep their own and draw their own; a redraw
/// pressed on one forgets the picture on that one. Nothing here is shared state.
/// </para>
/// </remarks>
public sealed class ServiceThumbnails
{
    /// <summary>
    /// How many pictures are held in memory in front of the disk.
    /// </summary>
    /// <remarks>
    /// <b>256 at roughly two kilobytes each is half a megabyte.</b> A miss reads one small file; the
    /// render is what the disk saves, not this.
    /// </remarks>
    public const int Capacity = 256;

    private readonly ConcurrentDictionary<string, Held> _held = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<Held?>>> _drawing = new(StringComparer.Ordinal);
    private readonly string? _directory;

    /// <summary>Creates a store held in memory only, for a test.</summary>
    public ServiceThumbnails()
    {
    }

    /// <summary>Creates a store that keeps its pictures under <paramref name="directory"/>.</summary>
    /// <param name="directory">Where the PNGs go; created when the first one is kept.</param>
    public ServiceThumbnails(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
    }

    /// <summary>One picture and what a browser needs to revalidate it.</summary>
    /// <param name="Bytes">The PNG.</param>
    /// <param name="ETag">Its entity tag, quoted and strong.</param>
    /// <param name="Drawn">When it was rendered.</param>
    public sealed record Held(byte[] Bytes, string ETag, DateTimeOffset Drawn);

    /// <summary>The key one picture is kept under, which is also its file name without the extension.</summary>
    /// <param name="layer">The layer's id.</param>
    /// <param name="width">Pixels across.</param>
    /// <param name="height">Pixels down.</param>
    /// <returns>The key.</returns>
    public static string KeyFor(Guid layer, int width, int height) =>
        string.Create(CultureInfo.InvariantCulture, $"{layer:N}-{width}x{height}");

    /// <summary>The picture, if one has been drawn.</summary>
    /// <param name="key">From <see cref="KeyFor"/>.</param>
    /// <returns>The picture, or null.</returns>
    public Held? Find(string key)
    {
        if (_held.TryGetValue(key, out Held? found))
        {
            return found;
        }

        if (PathOf(key) is not { } path || !File.Exists(path))
        {
            return null;
        }

        try
        {
            byte[] bytes = File.ReadAllBytes(path);

            // An empty file is a write that did not finish; drawing again is the repair.
            return bytes.Length == 0 ? null : Remember(key, new Held(bytes, Tag(bytes), File.GetLastWriteTimeUtc(path)));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Keeps a picture, in memory and on disk.</summary>
    /// <param name="key">From <see cref="KeyFor"/>.</param>
    /// <param name="bytes">The PNG.</param>
    /// <param name="now">The clock.</param>
    /// <returns>What was stored, so the caller can answer from it.</returns>
    /// <remarks>
    /// <b>A disk that refuses is not a failed thumbnail.</b> The picture is answered from memory and drawn
    /// again after a restart, which is what every picture did before ADR-071.
    /// </remarks>
    public Held Keep(string key, byte[] bytes, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        Held held = Remember(key, new Held(bytes, Tag(bytes), now));

        if (PathOf(key) is { } path)
        {
            try
            {
                Directory.CreateDirectory(_directory!);

                // Written beside and moved over, so a reader never sees half a PNG.
                string staging = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.partial";
                File.WriteAllBytes(staging, bytes);
                File.Move(staging, path, overwrite: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return held;
    }

    /// <summary>Draws a picture once however many callers want it at the same moment.</summary>
    /// <param name="key">From <see cref="KeyFor"/>.</param>
    /// <param name="draw">What draws and keeps it; not bound to any one caller's cancellation.</param>
    /// <param name="wait">How long this caller is willing to wait; leaving does not stop the draw.</param>
    /// <returns>The picture, or null when there is nothing to draw.</returns>
    public async Task<Held?> DrawOnceAsync(string key, Func<Task<Held?>> draw, CancellationToken wait)
    {
        ArgumentNullException.ThrowIfNull(draw);

        Lazy<Task<Held?>> mine = new(() => Run(key, draw));
        Lazy<Task<Held?>> running = _drawing.GetOrAdd(key, mine);

        return await running.Value.WaitAsync(wait).ConfigureAwait(false);
    }

    private async Task<Held?> Run(string key, Func<Task<Held?>> draw)
    {
        try
        {
            return await draw().ConfigureAwait(false);
        }
        finally
        {
            _drawing.TryRemove(key, out _);
        }
    }

    /// <summary>Forgets one layer's pictures, at every size, so the next request draws them again.</summary>
    /// <param name="layer">The layer's id.</param>
    /// <returns>How many pictures were forgotten, in memory and on disk together.</returns>
    public int Forget(Guid layer)
    {
        string prefix = layer.ToString("N", CultureInfo.InvariantCulture) + "-";
        int forgotten = 0;

        foreach (string key in _held.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            forgotten += _held.TryRemove(key, out _) ? 1 : 0;
        }

        if (_directory is not null && Directory.Exists(_directory))
        {
            foreach (string file in Directory.EnumerateFiles(_directory, prefix + "*.png"))
            {
                try
                {
                    File.Delete(file);
                    forgotten++;
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        return forgotten;
    }

    /// <summary>Forgets everything held in memory, for a test; the disk is untouched.</summary>
    public void Forget() => _held.Clear();

    private string? PathOf(string key) =>
        _directory is null ? null : Path.Combine(_directory, key + ".png");

    private Held Remember(string key, Held held)
    {
        _held[key] = held;

        // <b>Oldest-drawn rather than least-recently-used</b>: tracking reads would mean a write on every
        // hit, the path this exists to make cheap. An entry evicted here is read back from disk.
        while (_held.Count > Capacity)
        {
            KeyValuePair<string, Held> oldest = _held.OrderBy(e => e.Value.Drawn).First();
            _held.TryRemove(oldest);
        }

        return held;
    }

    /// <summary>A strong entity tag for these bytes.</summary>
    /// <remarks>
    /// <b>From the bytes rather than from the clock</b>, so a browser holding a picture that was drawn
    /// again identically revalidates with a 304 and no body.
    /// </remarks>
    private static string Tag(byte[] bytes) =>
        "\"" + Convert.ToHexString(SHA256.HashData(bytes).AsSpan(0, 8)).ToLowerInvariant() + "\"";
}
