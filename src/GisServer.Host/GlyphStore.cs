using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace GisServer.Host;

/// <summary>
/// The signed-distance-field glyphs a vector tile style needs to draw a label.
/// </summary>
/// <remarks>
/// <para>
/// <b>Without these the tile service cannot put a name on a map.</b> A MapLibre
/// or Mapbox GL style that sets <c>text-field</c> fetches
/// <c>{fontstack}/{range}.pbf</c> from the style's <c>glyphs</c> URL, and ours
/// had no such key. Labels are most of what anybody wants vector tiles for.
/// </para>
/// <para>
/// <b>Files on disk, generated once.</b> Q-15 requires air-gapped operation, so
/// pointing at a public glyph server is not available and the ranges ship in the
/// image. They are produced by <c>tools/make-glyphs.py</c>, which is the
/// specification for what is in them.
/// </para>
/// <para>
/// <b>Neither the fontstack nor the range reaches the filesystem as text.</b>
/// security.md: <em>filenames are data, never paths</em>. The fontstack is
/// matched against the stacks that exist and the matched one is used; the range
/// is parsed into two integers and the name is rebuilt from them. A request for
/// <c>../../appsettings.json</c> therefore fails to match a stack rather than
/// being sanitised, which is the difference between a check and a filter.
/// </para>
/// </remarks>
public sealed class GlyphStore
{
    /// <summary>The stack served when a style asks for one we do not have.</summary>
    /// <remarks>
    /// <b>Falling back beats 404, and this is a compatibility decision.</b> An
    /// ArcGIS style names <c>Arial Unicode MS Regular</c>; a Mapbox one names
    /// <c>Open Sans Regular</c>. We have neither and cannot ship either. A 404
    /// makes the client drop every label on the map and log a fetch error, which
    /// reads as a broken server. Drawing the label in the font we do have is
    /// visibly a substitution and obviously better than nothing.
    /// </remarks>
    public const string Fallback = "DejaVu Sans Regular";

    private readonly string _root;
    private readonly HashSet<string> _stacks;

    /// <summary>Opens the glyph directory beside the running binary.</summary>
    /// <param name="root">The directory holding one folder per font stack.</param>
    public GlyphStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        _root = root;

        _stacks = Directory.Exists(root)
            ? [.. Directory.EnumerateDirectories(root).Select(Path.GetFileName)
                .Where(n => n is { Length: > 0 })
                .Select(n => n!)]
            : [];
    }

    /// <summary>The directory the host ships glyphs in.</summary>
    /// <returns>The path, whether or not it exists.</returns>
    public static string BesideThisOne() =>
        Path.Combine(AppContext.BaseDirectory, "glyphs");

    /// <summary>The font stacks that exist, for the service document.</summary>
    public IReadOnlyCollection<string> Stacks => _stacks;

    /// <summary>Whether any glyphs shipped at all.</summary>
    public bool Any => _stacks.Count > 0;

    /// <summary>
    /// Reads one range of one stack.
    /// </summary>
    /// <param name="fontstack">
    /// What the style asked for. May be a comma-separated list, which is how a
    /// style expresses <em>this font, or that one</em>.
    /// </param>
    /// <param name="range">The range as it appeared in the URL, e.g. <c>0-255</c>.</param>
    /// <param name="bytes">The protobuf, when found.</param>
    /// <param name="served">Which stack actually answered.</param>
    /// <returns>True when a range was found.</returns>
    public bool TryRead(
        string? fontstack, string? range, out byte[] bytes, out string served)
    {
        bytes = [];
        served = string.Empty;

        if (!TryRange(range, out int start, out int end))
        {
            return false;
        }

        if (Resolve(fontstack) is not { } stack)
        {
            return false;
        }

        // Rebuilt from the parsed integers, so nothing the caller typed is a
        // path segment. Combine with a stack name that came from the directory
        // listing rather than from the URL.
        string path = Path.Combine(
            _root,
            stack,
            string.Create(CultureInfo.InvariantCulture, $"{start}-{end}.pbf"));

        if (!File.Exists(path))
        {
            return false;
        }

        bytes = File.ReadAllBytes(path);
        served = stack;
        return true;
    }

    /// <summary>
    /// The first stack in the list we have, or the fallback.
    /// </summary>
    private string? Resolve(string? fontstack)
    {
        if (_stacks.Count == 0)
        {
            return null;
        }

        foreach (string candidate in (fontstack ?? string.Empty).Split(
                     ',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (_stacks.Contains(candidate))
            {
                return candidate;
            }
        }

        return _stacks.Contains(Fallback) ? Fallback : _stacks.First();
    }

    /// <summary>
    /// Parses <c>start-end</c>, refusing anything that is not one 256-glyph
    /// range on the grid the format defines.
    /// </summary>
    /// <remarks>
    /// <b>Strict on purpose.</b> The ranges are a fixed grid — 0-255, 256-511 —
    /// and a client that asks for 100-200 has generated the URL wrongly. Being
    /// permissive here would turn a client bug into a file lookup with an
    /// attacker-influenced name, for no compatibility gained.
    /// </remarks>
    internal static bool TryRange(string? range, out int start, out int end)
    {
        start = 0;
        end = 0;

        if (range is null || range.Length is < 3 or > 12)
        {
            return false;
        }

        int dash = range.IndexOf('-', StringComparison.Ordinal);

        if (dash <= 0 || dash == range.Length - 1)
        {
            return false;
        }

        if (!int.TryParse(range[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out start)
            || !int.TryParse(
                range[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out end))
        {
            return false;
        }

        // NumberStyles.None already refuses a sign, whitespace and a thousands
        // separator, so "+0" and " 0" do not arrive here as zero.
        return start >= 0
            && start <= 0xFFFF
            && end == start + 255
            && start % 256 == 0;
    }
}
