using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;

namespace Graticula.Api.Tiles;

/// <summary>
/// The parts of a WMTS capabilities document a GetCapabilities asked for — OWS Common 1.1 §7.3.3 — and the
/// service provider section both of this server's WMTS documents write.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read, not ignored — 2026-10-06.</b> Both documents answered every <c>Sections</c> value with the whole
/// document, and neither had a <c>ServiceProvider</c> at all, so a request for that section alone got
/// everything except it. OGC's WMTS 1.0 suite failed <c>Sections.All</c> and <c>Sections.ServiceProvider</c> on
/// the image services' WMTS, the first time it was run.
/// </para>
/// <para>
/// A value OWS does not name is refused rather than dropped: a client asking for a section that does not exist
/// is told so, not handed a document without it and left to wonder.
/// </para>
/// </remarks>
public sealed class WmtsSections
{
    /// <summary>The section names OWS Common gives a WMTS capabilities document, in document order.</summary>
    public static readonly IReadOnlyList<string> Names =
        ["ServiceIdentification", "ServiceProvider", "OperationsMetadata", "Contents", "Themes"];

    private readonly HashSet<string> _asked;

    private WmtsSections(HashSet<string> asked) => _asked = asked;

    /// <summary>Every section — what a request that names none, or names <c>All</c>, receives.</summary>
    public static WmtsSections All { get; } = new(new HashSet<string>(Names, StringComparer.Ordinal));

    /// <summary>Whether the document carries a section.</summary>
    /// <param name="name">One of <see cref="Names"/>.</param>
    /// <returns>Whether it was asked for.</returns>
    public bool Includes(string name) => _asked.Contains(name);

    /// <summary>Reads a <c>Sections</c> value.</summary>
    /// <param name="value">The parameter, or null when absent.</param>
    /// <param name="sections">The sections asked for; <see cref="All"/> when absent.</param>
    /// <param name="fault">What is wrong with it.</param>
    /// <returns>Whether it could be read.</returns>
    public static bool TryRead(string? value, out WmtsSections? sections, out WmtsFault? fault)
    {
        fault = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            sections = All;
            return true;
        }

        HashSet<string> asked = new(StringComparer.Ordinal);

        foreach (string part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(part, "All", StringComparison.OrdinalIgnoreCase))
            {
                sections = All;
                return true;
            }

            if (Names.FirstOrDefault(n => string.Equals(n, part, StringComparison.OrdinalIgnoreCase)) is not { } known)
            {
                sections = null;
                fault = WmtsFault.Invalid(
                    "Sections", $"'{part}' is not a section of a WMTS capabilities document; they are {string.Join(", ", Names)} and All.");
                return false;
            }

            asked.Add(known);
        }

        sections = new WmtsSections(asked);
        return true;
    }

    /// <summary>Writes <c>ows:ServiceProvider</c>: who runs this server.</summary>
    /// <param name="xml">The document, positioned after <c>ows:ServiceIdentification</c>.</param>
    /// <param name="name">The provider's name.</param>
    /// <param name="site">Where the provider is, if known.</param>
    /// <remarks>
    /// <b>The name and an empty contact, which is what the schema requires and no more.</b> OWS makes
    /// <c>ProviderName</c> and <c>ServiceContact</c> mandatory and every part of the contact optional; this
    /// server has no contact a deployment has told it, and inventing one would be the document lying.
    /// </remarks>
    public static void WriteServiceProvider(XmlWriter xml, string name, string? site)
    {
        ArgumentNullException.ThrowIfNull(xml);

        xml.WriteStartElement("ows", "ServiceProvider", WmtsFault.Ows);
        xml.WriteElementString("ows", "ProviderName", WmtsFault.Ows, name);

        if (site is { Length: > 0 })
        {
            xml.WriteStartElement("ows", "ProviderSite", WmtsFault.Ows);
            xml.WriteAttributeString("xlink", "href", WmtsCapabilities.XLink, site);
            xml.WriteEndElement();
        }

        xml.WriteStartElement("ows", "ServiceContact", WmtsFault.Ows);
        xml.WriteEndElement();
        xml.WriteEndElement();
    }
}
