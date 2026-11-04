using System;
using System.Collections.Generic;

namespace Graticula.Cartography.Printing;

/// <summary>
/// A page a print is composed on — ADR-265 §5.5: its name as ArcGIS's print service names it, and its paper size.
/// </summary>
/// <param name="Name">The <c>Layout_Template</c> value, as the ArcGIS print service spells its default templates.</param>
/// <param name="WidthInches">The paper's width, in inches.</param>
/// <param name="HeightInches">The paper's height, in inches.</param>
/// <remarks>
/// <para>
/// <b>The names are ArcGIS's, the composition is ours.</b> A client sends <c>A4 Landscape</c> because the print widget
/// listed it from the task's choice list; what the page looks like is this server's own arrangement of the elements the
/// layout options name — a title, the map, a legend, a scale bar, a north arrow, the author and the copyright text. No
/// ArcGIS layout file is read or reproduced.
/// </para>
/// <para>
/// <b><see cref="MapOnly"/> is not a page</b>: it is the map alone at <c>exportOptions.outputSize</c>, which is what
/// ArcGIS's <c>MAP_ONLY</c> means, and it has no paper size.
/// </para>
/// </remarks>
public sealed record PageTemplate(string Name, double WidthInches, double HeightInches)
{
    /// <summary>The name of the map-alone output.</summary>
    public const string MapOnlyName = "MAP_ONLY";

    /// <summary>The map alone, at the output size asked for.</summary>
    public static PageTemplate MapOnly { get; } = new(MapOnlyName, 0, 0);

    /// <summary>Every template this server composes, <see cref="MapOnly"/> last, as ArcGIS lists its default.</summary>
    public static IReadOnlyList<PageTemplate> All { get; } =
    [
        new("A3 Landscape", 16.54, 11.69),
        new("A3 Portrait", 11.69, 16.54),
        new("A4 Landscape", 11.69, 8.27),
        new("A4 Portrait", 8.27, 11.69),
        new("Letter ANSI A Landscape", 11, 8.5),
        new("Letter ANSI A Portrait", 8.5, 11),
        new("Tabloid ANSI B Landscape", 17, 11),
        new("Tabloid ANSI B Portrait", 11, 17),
        MapOnly,
    ];

    /// <summary>Whether this is the map alone rather than a page.</summary>
    public bool IsMapOnly => string.Equals(Name, MapOnlyName, StringComparison.Ordinal);

    /// <summary>Whether the page is wider than it is tall.</summary>
    public bool IsLandscape => WidthInches > HeightInches;

    /// <summary>The template of this name, ignoring case; null for one this server does not have.</summary>
    /// <param name="name">The name a client sent, or null or empty for the default, <see cref="MapOnly"/>.</param>
    /// <returns>The template.</returns>
    /// <remarks>
    /// <b>Letter is also found as <c>Letter Landscape</c></b> and <c>Letter Portrait</c>, the names ArcGIS used before
    /// it wrote <c>ANSI A</c> into them, which older saved print settings still send.
    /// </remarks>
    public static PageTemplate? Named(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return MapOnly;
        }

        string asked = name.Trim();

        foreach (PageTemplate template in All)
        {
            if (string.Equals(template.Name, asked, StringComparison.OrdinalIgnoreCase))
            {
                return template;
            }
        }

        return asked.ToUpperInvariant() switch
        {
            "LETTER LANDSCAPE" => Named("Letter ANSI A Landscape"),
            "LETTER PORTRAIT" => Named("Letter ANSI A Portrait"),
            "TABLOID LANDSCAPE" => Named("Tabloid ANSI B Landscape"),
            "TABLOID PORTRAIT" => Named("Tabloid ANSI B Portrait"),
            _ => null,
        };
    }
}
