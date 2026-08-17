using System;
using System.Globalization;
using Graticula.Geometries;

namespace Graticula.Cartography;

/// <summary>
/// What a layer looks like before anybody has said what it should look like.
/// </summary>
/// <remarks>
/// <para>
/// <b>One place, because there were three</b> — ADR-033 §5b. The tile style painted every
/// polygon <c>#8fb8cc</c> and every line <c>#1f6f8b</c>; the feature service said nothing
/// at all, so each client invented a default of its own; and our console chose a third
/// colour in the browser. One layer, three appearances, and the server with an opinion
/// about none of them. ADR-028 §2A recorded the complaint this produces — *everything is
/// the same blue* — and it was true of the generated style for as long as the generated
/// style was one colour.
/// </para>
/// <para>
/// <b>Deterministic from the layer's name, and the name rather than its id.</b> An id is
/// unique but local: republishing the same data gives a new one, and the same layer on
/// another deployment has a different one, so the colour would move for reasons nobody
/// can see. A name is what a person knows the layer by, so the same layer is the same
/// colour tomorrow, after a restore, and on somebody else's server.
/// </para>
/// <para>
/// <b>Not <see cref="string.GetHashCode()"/>, and this is not a style preference.</b>
/// .NET salts string hashing per process, so a palette chosen that way would give a
/// different colour after every restart — the exact opposite of the property this type
/// exists for. FNV-1a is written out below: it is a few lines, it is stable across
/// processes, machines and framework versions, and nothing here needs it to be
/// cryptographic.
/// </para>
/// <para>
/// <b>The palette is Paul Tol's qualitative set</b>, which is published for exactly this
/// purpose: hues that stay distinguishable under the common forms of colour blindness and
/// that read against a light basemap. Choosing six hues by eye and calling them
/// accessible is what a server should not do on somebody's behalf.
/// </para>
/// <para>
/// This is a *generated* appearance, and every surface that serves it says so — the
/// distinction between "nobody has styled this" and "somebody chose exactly this" is what
/// makes it safe to change the generator later.
/// </para>
/// </remarks>
public static class GeneratedSymbology
{
    /// <summary>
    /// The qualitative palette, in the order it is drawn from.
    /// </summary>
    /// <remarks>
    /// Tol's bright scheme, minus the grey he reserves for *no data* and minus the red
    /// he reserves for emphasis: a layer that happens to hash to red would look like a
    /// warning it is not. Seven hues is enough that two layers colliding is uncommon and
    /// few enough that they stay apart.
    /// </remarks>
    public static readonly string[] Palette =
    [
        "#4477AA",   // blue
        "#228833",   // green
        "#CCBB44",   // yellow
        "#66CCEE",   // cyan
        "#AA3377",   // purple
        "#EE7733",   // orange
        "#009988",   // teal
    ];

    /// <summary>The colour this layer is drawn in until somebody says otherwise.</summary>
    /// <param name="layerName">The layer's name, which is what a person knows it by.</param>
    /// <returns>A hex colour from <see cref="Palette"/>.</returns>
    /// <exception cref="ArgumentException">The name is empty.</exception>
    public static string ColourOf(string layerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layerName);

        return Palette[(int)(Fingerprint(layerName) % (uint)Palette.Length)];
    }

    /// <summary>
    /// FNV-1a over the lower-cased name.
    /// </summary>
    /// <remarks>
    /// Lower-cased so that correcting a name's capitalisation does not recolour the map,
    /// and invariant so the answer does not depend on the server's locale — a Turkish
    /// locale lower-cases <c>I</c> to <c>ı</c>, which would make this deployment-specific
    /// in exactly the way the type promises it is not.
    /// </remarks>
    private static uint Fingerprint(string name)
    {
        const uint Offset = 2166136261;
        const uint Prime = 16777619;

        uint hash = Offset;

        foreach (char c in name.ToLowerInvariant())
        {
            hash = (hash ^ c) * Prime;
        }

        return hash;
    }

    /// <summary>
    /// The paint a generated appearance uses, per geometry kind.
    /// </summary>
    /// <param name="layerName">The layer being drawn.</param>
    /// <param name="geometry">Its geometry kind, which decides the shape of the paint.</param>
    /// <returns>The recipe both protocol faces project from.</returns>
    public static Appearance For(string layerName, GeometryKind geometry)
    {
        string colour = ColourOf(layerName);

        return geometry switch
        {
            GeometryKind.Point or GeometryKind.MultiPoint =>
                new Appearance(colour, Kind: AppearanceKind.Marker)
                {
                    // Four pixels: visible at a glance, small enough that a dense point
                    // layer is still readable as points rather than as a blob.
                    Size = 4,
                    Outline = "#FFFFFF",
                    OutlineWidth = 1,
                    Opacity = 0.9,
                },

            GeometryKind.LineString or GeometryKind.MultiLineString =>
                new Appearance(colour, Kind: AppearanceKind.Line)
                {
                    Size = 1.4,
                    Opacity = 0.95,
                },

            // Polygons, and anything we do not recognise: a fill reads as *something is
            // here* for every geometry, where a marker on a polygon reads as a mistake.
            _ => new Appearance(colour, Kind: AppearanceKind.Fill)
            {
                // Below half, because polygons overlap and a map of opaque fills hides
                // whatever is under the top one — including the ground.
                Opacity = 0.45,
                Outline = colour,
                OutlineWidth = 1,
            },
        };
    }

    /// <summary>Splits <c>#rrggbb</c> into bytes, for a face that wants numbers.</summary>
    /// <param name="hex">A six-digit hex colour with a leading <c>#</c>.</param>
    /// <returns>Red, green and blue.</returns>
    /// <exception cref="ArgumentException">It is not a six-digit hex colour.</exception>
    public static (byte Red, byte Green, byte Blue) Bytes(string hex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hex);

        if (hex.Length != 7 || hex[0] != '#')
        {
            throw new ArgumentException(
                $"'{hex}' is not a #rrggbb colour. This type produces its own colours, so a "
                + "value reaching here in another shape came from configuration or a style.",
                nameof(hex));
        }

        static byte Pair(ReadOnlySpan<char> pair) =>
            byte.Parse(pair, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        return (Pair(hex.AsSpan(1, 2)), Pair(hex.AsSpan(3, 2)), Pair(hex.AsSpan(5, 2)));
    }
}

/// <summary>Which shape of paint an appearance is.</summary>
public enum AppearanceKind
{
    /// <summary>A point symbol.</summary>
    Marker,

    /// <summary>A stroked line.</summary>
    Line,

    /// <summary>A filled area with an outline.</summary>
    Fill,
}

/// <summary>
/// A single symbol, in terms neither protocol owns.
/// </summary>
/// <remarks>
/// <b>Deliberately not a symbology model</b> — ADR-033 §2D rejected inventing one, and
/// this is not it reappearing. It carries exactly what a *generated* default needs so
/// that two writers can project the same decision, and it is never stored: the canonical
/// document is MapLibre (§5a), and an unstyled layer has no canonical document at all.
/// </remarks>
/// <param name="Colour">The main colour, as <c>#rrggbb</c>.</param>
/// <param name="Kind">Which shape of paint this is.</param>
public sealed record Appearance(string Colour, AppearanceKind Kind)
{
    /// <summary>Radius in pixels for a marker, width in pixels for a line.</summary>
    public double Size { get; init; } = 1;

    /// <summary>How opaque the main paint is, from 0 to 1.</summary>
    public double Opacity { get; init; } = 1;

    /// <summary>The outline colour, or null for no outline.</summary>
    public string? Outline { get; init; }

    /// <summary>The outline width in pixels, when there is an outline.</summary>
    public double OutlineWidth { get; init; }
}
