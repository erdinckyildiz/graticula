using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace Graticula.Api.Wfs;

/// <summary>
/// Turns an XML-encoded request into the same key-value pairs a KVP one arrives as.
/// </summary>
/// <remarks>
/// <para>
/// <b>One binder, two encodings.</b> WFS defines the same operations twice — once
/// as a query string and once as an XML document — and the obvious implementation
/// is two binders that drift. This reduces the XML form to the KVP form and hands
/// it to <see cref="WfsRequest.TryParse"/>, so version negotiation, format
/// negotiation, paging and every refusal message are written once and behave
/// identically whichever way the request arrived.
/// </para>
/// <para>
/// <b>POST is not a nicety.</b> A client with a real filter — a map extent
/// intersected with a few attribute tests — will exceed what a query string can
/// carry, and GDAL switches to POST when it does. A KVP-only WFS works until the
/// filter gets interesting.
/// </para>
/// <para>
/// <b>The body is read through <see cref="SafeXml"/> like every other document
/// here</b>, so the entity and doctype protections apply to the request envelope
/// and not only to the filter inside it.
/// </para>
/// </remarks>
public static class WfsXmlRequest
{
    /// <summary>Reads a request body.</summary>
    /// <param name="body">The POST body.</param>
    /// <param name="parameters">The equivalent key-value pairs.</param>
    /// <param name="fault">Why it was refused.</param>
    /// <returns>Whether it read.</returns>
    public static bool TryRead(
        Stream body,
        out IReadOnlyDictionary<string, string> parameters,
        out WfsFault? fault)
    {
        ArgumentNullException.ThrowIfNull(body);

        Dictionary<string, string> kvp = new(StringComparer.OrdinalIgnoreCase);

        parameters = kvp;
        fault = null;

        XElement root;

        try
        {
            using XmlReader reader = SafeXml.Read(body);
            root = XElement.Load(reader, LoadOptions.None);
        }
        catch (XmlException e)
        {
            fault = WfsFault.Invalid(
                "request", $"The request body is not well-formed XML: {e.Message}");

            return false;
        }

        foreach (XAttribute attribute in root.Attributes())
        {
            if (!attribute.IsNamespaceDeclaration)
            {
                kvp[attribute.Name.LocalName] = attribute.Value;
            }
        }

        // <b>The element names the operation, and an attribute cannot argue.</b>
        // This was set before the attribute loop, so a `request="GetCapabilities"`
        // attribute on a `wfs:GetFeature` element overwrote it and the server
        // answered the attribute. No authorization boundary moved — every
        // operation here is anonymous — but the two encodings disagreed, and the
        // KVP form has no way to express the same confusion. Found by an
        // independent reviewer looking for exactly that asymmetry.
        kvp["request"] = root.Name.LocalName;

        XNamespace ows = WfsNames.Ows;
        XNamespace wfs = WfsNames.Wfs;
        XNamespace fes = WfsNames.Fes;

        if (root.Element(ows + "AcceptVersions") is { } accept)
        {
            kvp["acceptversions"] = string.Join(
                ',', accept.Elements(ows + "Version").Select(v => v.Value));
        }

        // DescribeFeatureType carries its types as elements rather than as an
        // attribute, and both spellings of the element name are in use.
        List<string> typeNames =
        [
            .. root.Elements(wfs + "TypeName").Select(t => t.Value),
            .. root.Elements(wfs + "TypeNames").Select(t => t.Value),
        ];

        XElement? query = root.Element(wfs + "Query");

        if (query is not null)
        {
            foreach (XAttribute attribute in query.Attributes())
            {
                if (!attribute.IsNamespaceDeclaration)
                {
                    kvp[attribute.Name.LocalName] = attribute.Value;
                }
            }

            if (query.Element(fes + "Filter") is { } filter)
            {
                kvp["filter"] = filter.ToString(SaveOptions.DisableFormatting);
            }

            if (query.Element(fes + "SortBy") is { } sortBy)
            {
                kvp["sortby"] = string.Join(',', SortKeys(sortBy, fes));
            }

            List<string> properties =
            [
                .. query.Elements(wfs + "PropertyName").Select(p => p.Value),
            ];

            if (properties.Count > 0)
            {
                kvp["propertyname"] = string.Join(',', properties);
            }
        }

        // <b>The XML form binds its prefixes the way XML does, and the KVP form
        // needs them spelled out.</b> A `typeNames="ns98:roads"` attribute means
        // nothing without the `xmlns:ns98` declaration that goes with it, and the
        // declaration is on an ancestor element rather than in the value. Reducing
        // one encoding to the other has to carry the binding across or the POST
        // path repeats the defect the conformance suite found in the GET path.
        List<string> declared = [];

        List<XElement> scopes = [root];

        if (query is not null)
        {
            scopes.Add(query);
        }

        foreach (XElement scope in scopes)
        {
            foreach (XAttribute attribute in scope.Attributes())
            {
                if (attribute.IsNamespaceDeclaration)
                {
                    string prefix = attribute.Name.LocalName == "xmlns"
                        ? string.Empty
                        : attribute.Name.LocalName;

                    declared.Add($"xmlns({prefix},{attribute.Value})");
                }
            }
        }

        if (declared.Count > 0)
        {
            kvp["namespaces"] = string.Join(',', declared);
        }

        if (typeNames.Count > 0)
        {
            // The Query element's own typeNames attribute is the authoritative one
            // when both appear, and it has already been copied above.
            kvp.TryAdd("typenames", string.Join(',', typeNames));
        }

        // <b>A stored query is an element rather than a parameter.</b>
        // <c>wfs:StoredQuery</c> carries its id as an attribute and its arguments
        // as children, which is a different shape from every other operation and
        // is why it is unpicked here rather than by the attribute loop.
        if (root.Element(wfs + "StoredQuery") is { } stored)
        {
            kvp["request"] = nameof(WfsOperation.GetFeature);
            kvp["storedquery_id"] = (string?)stored.Attribute("id") ?? string.Empty;

            foreach (XElement parameter in stored.Elements(wfs + "Parameter"))
            {
                if ((string?)parameter.Attribute("name") is { Length: > 0 } name)
                {
                    kvp[name] = parameter.Value;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Flattens <c>fes:SortBy</c> into the spelling the KVP form uses.
    /// </summary>
    /// <remarks>
    /// The KVP form is <c>property ASC,other DESC</c> and the XML form is a
    /// property element beside an order element. Reducing one to the other means
    /// the sort is validated in one place.
    /// </remarks>
    private static IEnumerable<string> SortKeys(XElement sortBy, XNamespace fes)
    {
        foreach (XElement property in sortBy.Elements(fes + "SortProperty"))
        {
            string? name = property.Element(fes + "ValueReference")?.Value
                ?? property.Element(fes + "PropertyName")?.Value;

            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            string order = property.Element(fes + "SortOrder")?.Value ?? "ASC";

            yield return $"{name.Trim()} {order.Trim()}";
        }
    }
}
