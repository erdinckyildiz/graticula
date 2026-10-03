using System;
using System.Linq;
using System.Xml.Linq;

namespace Graticula.Api.Wfs;

/// <summary>
/// What differs between the WFS versions this server speaks — ADR-168: the namespaces, the element a feature is a member
/// of, the GML media type and the schemas a document points at. 2.0.0 is the server's own; 1.1.0 is what older ArcGIS,
/// FME and MapInfo clients send.
/// </summary>
/// <param name="Version">The version, as a request names it.</param>
/// <param name="Wfs">The WFS namespace.</param>
/// <param name="Gml">The GML namespace.</param>
/// <param name="Ows">The OWS Common namespace.</param>
/// <param name="Filter">The filter namespace: FES 2.0, or OGC Filter 1.1.</param>
/// <param name="GmlMediaType">The output format a GML answer is named by.</param>
/// <param name="WfsSchema">Where the WFS schema is published.</param>
/// <param name="GmlSchema">Where the GML schema is published.</param>
/// <param name="OwsVersion">The OWS Common version an exception report states.</param>
public sealed record WfsDialect(
    string Version,
    string Wfs,
    string Gml,
    string Ows,
    string Filter,
    string GmlMediaType,
    string WfsSchema,
    string GmlSchema,
    string OwsVersion)
{
    /// <summary>WFS 2.0.0 with GML 3.2 and FES 2.0 — the server's own.</summary>
    public static WfsDialect V200 { get; } = new(
        WfsNames.Version, WfsNames.Wfs, WfsNames.Gml, WfsNames.Ows, WfsNames.Fes, WfsNames.GmlMediaType,
        "http://schemas.opengis.net/wfs/2.0/wfs.xsd", "http://schemas.opengis.net/gml/3.2.1/gml.xsd", "2.0.0");

    /// <summary>WFS 1.1.0 with GML 3.1.1, OGC Filter 1.1 and OWS 1.0.</summary>
    public static WfsDialect V110 { get; } = new(
        "1.1.0", "http://www.opengis.net/wfs", "http://www.opengis.net/gml", "http://www.opengis.net/ows",
        "http://www.opengis.net/ogc", "text/xml; subtype=gml/3.1.1",
        "http://schemas.opengis.net/wfs/1.1.0/wfs.xsd", "http://schemas.opengis.net/gml/3.1.1/base/gml.xsd", "1.0.0");

    /// <summary>Whether this is WFS 1.1.0.</summary>
    public bool IsLegacy => Version == "1.1.0";

    /// <summary>The versions this server speaks, newest first.</summary>
    public static string[] Versions { get; } = [V200.Version, V110.Version];

    /// <summary>The dialect of a version, or null when it is not one this server speaks.</summary>
    /// <param name="version">The version a request names.</param>
    /// <returns>The dialect.</returns>
    public static WfsDialect? Of(string? version) => version?.Trim() switch
    {
        "2.0.0" or "2.0" => V200,
        "1.1.0" or "1.1" => V110,
        _ => null,
    };

    /// <summary>
    /// An OGC Filter 1.1 document written as FES 2.0 — ADR-168 — so one reader reads both: the <c>ogc</c> namespace
    /// to <c>fes</c>, <c>PropertyName</c> to <c>ValueReference</c>, <c>FeatureId</c> and <c>GmlObjectId</c> to
    /// <c>ResourceId</c>, <c>PropertyIsLike</c>'s <c>escape</c> to <c>escapeChar</c>, and GML 3.1.1 to GML 3.2,
    /// whose geometry elements are the same. A FES 2.0 document is returned as it is.
    /// </summary>
    /// <param name="root">The filter.</param>
    /// <returns>The filter in FES 2.0.</returns>
    public static XElement ToFes20(XElement root)
    {
        ArgumentNullException.ThrowIfNull(root);
        XNamespace ogc = V110.Filter, fes = WfsNames.Fes, gml311 = V110.Gml, gml32 = WfsNames.Gml;

        if (!root.DescendantsAndSelf().Any(e => e.Name.Namespace == ogc || e.Name.Namespace == gml311))
        {
            return root;
        }

        XElement copy = new(root);

        foreach (XElement e in copy.DescendantsAndSelf().ToList())
        {
            if (e.Name.Namespace == ogc)
            {
                string local = e.Name.LocalName;

                if (local is "FeatureId" or "GmlObjectId")
                {
                    string? id = (string?)e.Attribute("fid") ?? (string?)e.Attribute(gml311 + "id") ?? (string?)e.Attribute("id");
                    e.Name = fes + "ResourceId";
                    e.RemoveAttributes();

                    if (id is not null)
                    {
                        e.SetAttributeValue("rid", id);
                    }

                    continue;
                }

                e.Name = fes + (local == "PropertyName" ? "ValueReference" : local);

                if (local == "PropertyIsLike" && e.Attribute("escape") is { } escape)
                {
                    e.SetAttributeValue("escapeChar", escape.Value);
                    escape.Remove();
                }
            }
            else if (e.Name.Namespace == gml311)
            {
                e.Name = gml32 + e.Name.LocalName;
            }

            foreach (XAttribute attribute in e.Attributes().Where(a => a.Name.Namespace == gml311).ToList())
            {
                attribute.Remove();
                e.SetAttributeValue(gml32 + attribute.Name.LocalName, attribute.Value);
            }

            foreach (XAttribute declaration in e.Attributes().Where(a => a.IsNamespaceDeclaration
                && (a.Value == V110.Filter || a.Value == V110.Gml)).ToList())
            {
                declaration.Remove();
            }
        }

        return copy;
    }
}
