using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Xml;
using Graticula.Geometries;

namespace Graticula.Api.Wms;

/// <summary>
/// What this server publishes, in whichever version was asked for.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two documents, not one document with two spellings.</b> 1.1.1 and 1.3.0 differ
/// in the root element, the namespace, the validation mechanism (DTD against
/// schema), the element that carries a geographic extent, the name of the CRS
/// element, and where a dimension's values live. A single writer with conditionals
/// at each of those points is a writer where a client-specific bug is invisible; two
/// methods sharing the layer walk is the smaller lie.
/// </para>
/// <para>
/// <b>The layer list is already filtered.</b> Sharing is applied by the host before
/// anything reaches here, exactly as WFS does it, so an anonymous client sees the
/// public layers and learns nothing about the rest.
/// </para>
/// </remarks>
public static class CapabilitiesDocument
{
    /// <summary>Every CRS this server will draw in, whatever a layer is stored in.</summary>
    /// <remarks>
    /// <b>Three, and the third is not a duplicate.</b> <c>CRS:84</c> is WGS 84 in
    /// longitude/latitude order, and it exists because 1.3.0 made <c>EPSG:4326</c>
    /// latitude first. Clients that would rather not think about axis order ask for
    /// it by name, and a server that omits it forces them to think about it.
    /// Reprojection is the database's (<c>ST_Transform</c>), so any code PostGIS
    /// knows would work; these are the three worth advertising.
    /// </remarks>
    public static readonly string[] CoordinateSystems = ["EPSG:4326", "EPSG:3857", "CRS:84"];

    /// <summary>The formats <c>GetFeatureInfo</c> will answer in.</summary>
    public static readonly string[] InfoFormats =
        ["text/plain", "application/json", "text/html"];

    /// <summary>Writes the document.</summary>
    /// <param name="version">Which version.</param>
    /// <param name="endpoint">This service's own address, absolute.</param>
    /// <param name="title">What to call the service.</param>
    /// <param name="layers">The layers the caller may see.</param>
    /// <param name="limits">The bounds to publish.</param>
    /// <param name="contact">Who to ask about this server, or nobody.</param>
    /// <returns>The XML.</returns>
    public static string Write(
        WmsVersion version,
        string endpoint,
        string title,
        IReadOnlyList<WmsLayer> layers,
        WmsLimits limits,
        WmsContact contact = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentNullException.ThrowIfNull(layers);

        using Utf8Text text = new();

        XmlWriterSettings settings = new()
        {
            Indent = true,
            IndentChars = "  ",
            Encoding = Encoding.UTF8,
        };

        using (XmlWriter writer = XmlWriter.Create(text, settings))
        {
            if (version == WmsVersion.V130)
            {
                Write130(writer, endpoint, title, layers, limits, contact);
            }
            else
            {
                Write111(writer, endpoint, title, layers, limits, contact);
            }
        }

        return text.ToString();
    }

    private static void Write130(
        XmlWriter writer,
        string endpoint,
        string title,
        IReadOnlyList<WmsLayer> layers,
        WmsLimits limits,
        WmsContact contact)
    {
        writer.WriteStartElement("WMS_Capabilities", WmsNames.Wms);
        writer.WriteAttributeString("version", "1.3.0");
        writer.WriteAttributeString("xmlns", "xlink", null, WmsNames.Xlink);
        writer.WriteAttributeString("xmlns", "xsi", null, WmsNames.Xsi);
        writer.WriteAttributeString(
            "xsi", "schemaLocation", null, $"{WmsNames.Wms} {WmsNames.SchemaLocation130}");

        WriteService(writer, endpoint, title, limits, WmsVersion.V130, contact);

        writer.WriteStartElement("Capability");
        WriteRequests(writer, endpoint, WmsVersion.V130);

        writer.WriteStartElement("Exception");
        writer.WriteElementString("Format", "XML");
        writer.WriteEndElement();

        // The root layer, which has a title and no name. A named root would be a
        // layer a client could ask for, and there is nothing behind it to draw.
        writer.WriteStartElement("Layer");
        writer.WriteElementString("Title", title);

        foreach (string crs in CoordinateSystems)
        {
            writer.WriteElementString("CRS", crs);
        }

        WriteGeographicBox(writer, Whole(layers), WmsVersion.V130);

        foreach (WmsLayer layer in layers)
        {
            WriteLayer130(writer, layer, endpoint);
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void Write111(
        XmlWriter writer,
        string endpoint,
        string title,
        IReadOnlyList<WmsLayer> layers,
        WmsLimits limits,
        WmsContact contact)
    {
        // <b>The DOCTYPE is not decoration.</b> 1.1.1 is DTD-validated, and clients
        // of that era check. A 1.1.1 document without it is refused by some of the
        // very tools this version exists to serve.
        writer.WriteDocType("WMT_MS_Capabilities", null, WmsNames.Dtd111, null);

        writer.WriteStartElement("WMT_MS_Capabilities");
        writer.WriteAttributeString("version", "1.1.1");

        WriteService(writer, endpoint, title, limits, WmsVersion.V111, contact);

        writer.WriteStartElement("Capability");
        WriteRequests(writer, endpoint, WmsVersion.V111);

        writer.WriteStartElement("Exception");
        writer.WriteElementString("Format", WmsNames.ExceptionMediaType111);
        writer.WriteEndElement();

        writer.WriteStartElement("Layer");
        writer.WriteElementString("Title", title);

        foreach (string crs in CoordinateSystems)
        {
            writer.WriteElementString("SRS", crs);
        }

        WriteGeographicBox(writer, Whole(layers), WmsVersion.V111);

        foreach (WmsLayer layer in layers)
        {
            WriteLayer111(writer, layer, endpoint);
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteService(
        XmlWriter writer,
        string endpoint,
        string title,
        WmsLimits limits,
        WmsVersion version,
        WmsContact contact)
    {
        writer.WriteStartElement("Service");
        writer.WriteElementString("Name", version == WmsVersion.V130 ? "WMS" : "OGC:WMS");
        writer.WriteElementString("Title", title);
        writer.WriteElementString(
            "Abstract",
            "Maps drawn from this server's own published layers, using each layer's stored "
            + "symbology. Read-only.");

        WriteKeywords(
            writer,
            // <b>What this server is, not what somebody hopes it is found by.</b> The
            // WMS 1.3.0 suite recommends a keyword list at service level so a catalogue
            // can index the server, and the temptation is to write "GIS, maps, spatial"
            // — words true of every server of this kind and therefore useless for
            // telling one from another. These five say what a client can do here.
            ["WMS", "GetMap", "GetFeatureInfo", "vector", "PostGIS"]);

        WriteOnlineResource(writer, endpoint);

        WriteContact(writer, contact);

        // <b>Published rather than discovered.</b> A client that learns the limit
        // from the document never sends a request that hits it; one that does not
        // learns by being refused, which it reports as a server fault.
        writer.WriteElementString(
            "MaxWidth", limits.MaximumWidth.ToString(CultureInfo.InvariantCulture));
        writer.WriteElementString(
            "MaxHeight", limits.MaximumHeight.ToString(CultureInfo.InvariantCulture));

        writer.WriteEndElement();
    }

    private static void WriteRequests(XmlWriter writer, string endpoint, WmsVersion version)
    {
        writer.WriteStartElement("Request");

        writer.WriteStartElement("GetCapabilities");
        writer.WriteElementString(
            "Format",
            version == WmsVersion.V130
                ? WmsNames.CapabilitiesMediaType130
                : WmsNames.CapabilitiesMediaType111);

        WriteHttpGet(writer, endpoint);
        writer.WriteEndElement();

        writer.WriteStartElement("GetMap");
        writer.WriteElementString("Format", "image/png");
        writer.WriteElementString("Format", "image/jpeg");
        WriteHttpGet(writer, endpoint);
        writer.WriteEndElement();

        writer.WriteStartElement("GetFeatureInfo");

        foreach (string format in InfoFormats)
        {
            writer.WriteElementString("Format", format);
        }

        WriteHttpGet(writer, endpoint);
        writer.WriteEndElement();

        writer.WriteEndElement();
    }

    /// <summary>
    /// The address a client appends its parameters to.
    /// </summary>
    /// <remarks>
    /// <b>It must end in <c>?</c> or <c>&amp;</c>, and that is a requirement rather
    /// than a convention.</b> WMS 1.3.0 (OGC 06-042) §6.3.3: an OnlineResource URL
    /// for HTTP GET is a **URL prefix**, so a client builds a request by concatenating
    /// its parameters onto it without having to decide whether a separator is needed.
    /// A bare address works with every client that adds the <c>?</c> itself and
    /// silently produces <c>/wmsservice=WMS</c> in one that does not.
    /// </remarks>
    /// <param name="endpoint">This service's address.</param>
    /// <returns>The prefix.</returns>
    private static string Prefix(string endpoint) =>
        endpoint.EndsWith('?') || endpoint.EndsWith('&')
            ? endpoint
            : endpoint + (endpoint.Contains('?', StringComparison.Ordinal) ? "&" : "?");

    private static void WriteHttpGet(XmlWriter writer, string endpoint)
    {
        writer.WriteStartElement("DCPType");
        writer.WriteStartElement("HTTP");
        writer.WriteStartElement("Get");
        WriteOnlineResource(writer, Prefix(endpoint));
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    /// <summary>
    /// Writes <c>ContactInformation</c>, or nothing when nobody has been named.
    /// </summary>
    /// <remarks>
    /// <b>Nothing rather than something plausible.</b> See <see cref="WmsContact"/>: the
    /// CITE suite recommends this element and the way to satisfy it without knowing the
    /// answer is to invent one, which misleads a client that acts on it. An absent
    /// element says *this deployment has not said*, which is true and useful.
    /// </remarks>
    private static void WriteContact(XmlWriter writer, WmsContact contact)
    {
        if (!contact.IsStated)
        {
            return;
        }

        writer.WriteStartElement("ContactInformation");

        if (!string.IsNullOrWhiteSpace(contact.Person)
            || !string.IsNullOrWhiteSpace(contact.Organization))
        {
            writer.WriteStartElement("ContactPersonPrimary");

            // <b>Both children, in this order, even when one is empty.</b> The schema
            // makes ContactPerson and ContactOrganization required inside the wrapper, so
            // writing only the one that is set produces a document that fails validation
            // — which is a worse outcome than an empty element.
            writer.WriteElementString("ContactPerson", contact.Person ?? string.Empty);
            writer.WriteElementString(
                "ContactOrganization", contact.Organization ?? string.Empty);

            writer.WriteEndElement();
        }

        if (!string.IsNullOrWhiteSpace(contact.Position))
        {
            writer.WriteElementString("ContactPosition", contact.Position);
        }

        if (!string.IsNullOrWhiteSpace(contact.Phone))
        {
            writer.WriteElementString("ContactVoiceTelephone", contact.Phone);
        }

        if (!string.IsNullOrWhiteSpace(contact.Email))
        {
            writer.WriteElementString("ContactElectronicMailAddress", contact.Email);
        }

        writer.WriteEndElement();
    }

    /// <summary>Writes a <c>KeywordList</c>, or nothing when there is nothing to say.</summary>
    /// <remarks>
    /// <b>Recommended rather than required by WMS 1.3.0, and it is a recommendation worth
    /// meeting.</b> A catalogue harvesting this document has the title and the abstract
    /// and no vocabulary; a layer picker with a search box has nothing to search. What
    /// makes it worth writing rather than filling in is that every keyword here is
    /// derived from something the server knows — the geometry it holds, the service it
    /// belongs to — so none of them can become false without the layer changing.
    /// </remarks>
    private static void WriteKeywords(XmlWriter writer, List<string> keywords)
    {
        if (keywords.Count == 0)
        {
            return;
        }

        writer.WriteStartElement("KeywordList");

        foreach (string keyword in keywords)
        {
            writer.WriteElementString("Keyword", keyword);
        }

        writer.WriteEndElement();
    }

    /// <summary>What a layer can honestly be searched by.</summary>
    /// <remarks>
    /// <b>Derived, so it cannot go stale.</b> The geometry kind, the service the layer
    /// belongs to, and its folder if it has one. A hand-written keyword list on a layer
    /// nobody revisits is a list that describes what the layer used to hold.
    /// </remarks>
    private static List<string> KeywordsOf(WmsLayer layer)
    {
        List<string> keywords = [layer.GeometryType.ToString()];

        if (layer.Name is { Length: > 0 } name
            && !string.Equals(name, layer.GeometryType.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            keywords.Add(name);
        }

        return keywords;
    }

    /// <summary>
    /// A layer's abstract, from what the server knows about it.
    /// </summary>
    /// <remarks>
    /// <b>The catalogue has no description field for a layer, so this states facts
    /// instead of repeating a title.</b> WMS 1.3.0 recommends an abstract on every named
    /// layer and the recommendation is about a client's layer picker: a title of
    /// `tr_il` tells a person nothing, and *polygon features in EPSG:4326, with a time
    /// dimension* tells them whether it is the layer they want. **Nothing here is
    /// invented** — every clause is read off the layer, so the sentence changes when the
    /// layer does.
    /// </remarks>
    private static string AbstractOf(WmsLayer layer)
    {
        string geometry = layer.GeometryType switch
        {
            GeometryKind.Point or GeometryKind.MultiPoint => "Point features",
            GeometryKind.LineString or GeometryKind.MultiLineString => "Line features",
            GeometryKind.Polygon or GeometryKind.MultiPolygon => "Polygon features",
            _ => "Features",
        };

        string text =
            $"{geometry} held in EPSG:{layer.Srid.ToString(CultureInfo.InvariantCulture)}, "
            + "drawn with this layer's own stored symbology.";

        if (layer.Time is { } time && time.ExtentText.Length > 0)
        {
            text += " Has a time dimension, so a TIME parameter selects an instant or a range.";
        }

        if (layer.Queryable)
        {
            text += " GetFeatureInfo answers for this layer.";
        }

        return text;
    }

    private static void WriteOnlineResource(XmlWriter writer, string href)
    {
        writer.WriteStartElement("OnlineResource");
        writer.WriteAttributeString("xmlns", "xlink", null, WmsNames.Xlink);
        writer.WriteAttributeString("xlink", "type", WmsNames.Xlink, "simple");
        writer.WriteAttributeString("xlink", "href", WmsNames.Xlink, href);
        writer.WriteEndElement();
    }

    private static void WriteLayer130(XmlWriter writer, WmsLayer layer, string endpoint)
    {
        writer.WriteStartElement("Layer");
        writer.WriteAttributeString("queryable", layer.Queryable ? "1" : "0");

        writer.WriteElementString("Name", layer.Name);
        writer.WriteElementString("Title", layer.Title);

        // A description the catalogue holds wins; otherwise the layer describes itself.
        writer.WriteElementString(
            "Abstract",
            layer.Abstract is { Length: > 0 } description ? description : AbstractOf(layer));

        WriteKeywords(writer, KeywordsOf(layer));

        writer.WriteElementString("CRS", $"EPSG:{layer.Srid.ToString(CultureInfo.InvariantCulture)}");

        WriteGeographicBox(writer, layer.Geographic, WmsVersion.V130);
        WriteBoundingBox(writer, layer, WmsVersion.V130);

        // <b>1.3.0 puts a dimension's values inside the Dimension element.</b> 1.1.1
        // splits them into Dimension and Extent. Getting that backwards produces a
        // document that validates and carries no times.
        if (layer.Time is { } time && time.ExtentText.Length > 0)
        {
            writer.WriteStartElement("Dimension");
            writer.WriteAttributeString("name", TimeDimension.Name);
            writer.WriteAttributeString("units", TimeDimension.Units);
            writer.WriteAttributeString("default", time.DefaultText);
            writer.WriteString(time.ExtentText);
            writer.WriteEndElement();
        }

        WriteStyle(writer, layer, endpoint, WmsVersion.V130);
        writer.WriteEndElement();
    }

    private static void WriteLayer111(XmlWriter writer, WmsLayer layer, string endpoint)
    {
        writer.WriteStartElement("Layer");
        writer.WriteAttributeString("queryable", layer.Queryable ? "1" : "0");

        writer.WriteElementString("Name", layer.Name);
        writer.WriteElementString("Title", layer.Title);

        writer.WriteElementString(
            "Abstract",
            layer.Abstract is { Length: > 0 } description ? description : AbstractOf(layer));

        WriteKeywords(writer, KeywordsOf(layer));

        writer.WriteElementString("SRS", $"EPSG:{layer.Srid.ToString(CultureInfo.InvariantCulture)}");

        WriteGeographicBox(writer, layer.Geographic, WmsVersion.V111);
        WriteBoundingBox(writer, layer, WmsVersion.V111);

        if (layer.Time is { } time && time.ExtentText.Length > 0)
        {
            writer.WriteStartElement("Dimension");
            writer.WriteAttributeString("name", TimeDimension.Name);
            writer.WriteAttributeString("units", TimeDimension.Units);
            writer.WriteEndElement();

            writer.WriteStartElement("Extent");
            writer.WriteAttributeString("name", TimeDimension.Name);
            writer.WriteAttributeString("default", time.DefaultText);
            writer.WriteString(time.ExtentText);
            writer.WriteEndElement();
        }

        WriteStyle(writer, layer, endpoint, WmsVersion.V111);
        writer.WriteEndElement();
    }

    /// <summary>
    /// The one style a layer has, with the address of its legend.
    /// </summary>
    /// <remarks>
    /// <b>Named <c>default</c>, because a style with no name cannot be asked for and
    /// several clients will not draw a legend for one.</b> ADR-041 §5.2: this server
    /// has one symbology per layer and refuses any other name, so the name is a
    /// label rather than a choice.
    /// </remarks>
    private static void WriteStyle(
        XmlWriter writer, WmsLayer layer, string endpoint, WmsVersion version)
    {
        writer.WriteStartElement("Style");
        writer.WriteElementString("Name", "default");
        writer.WriteElementString("Title", "Default");

        writer.WriteStartElement("LegendURL");
        writer.WriteAttributeString("width", "20");
        writer.WriteAttributeString("height", "20");
        writer.WriteElementString("Format", "image/png");

        WriteOnlineResource(
            writer,
            $"{endpoint}?service=WMS&version={WmsNames.Text(version)}"
            + $"&request=GetLegendGraphic&layer={Uri.EscapeDataString(layer.Name)}"
            + "&format=image/png");

        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    /// <summary>
    /// The extent in WGS 84, in the element each version has for it.
    /// </summary>
    /// <remarks>
    /// <b>Always longitude first, in both versions.</b> 1.3.0's
    /// <c>EX_GeographicBoundingBox</c> names its children rather than ordering them,
    /// which is the specification quietly conceding that the axis rule it introduced
    /// is a trap. 1.1.1's <c>LatLonBoundingBox</c> is named for latitude and carries
    /// longitude in <c>minx</c>, which is the same concession made worse.
    /// </remarks>
    private static void WriteGeographicBox(XmlWriter writer, Envelope? extent, WmsVersion version)
    {
        if (extent is not { IsEmpty: false } box)
        {
            return;
        }

        if (version == WmsVersion.V130)
        {
            writer.WriteStartElement("EX_GeographicBoundingBox");
            writer.WriteElementString("westBoundLongitude", Number(box.MinX));
            writer.WriteElementString("eastBoundLongitude", Number(box.MaxX));
            writer.WriteElementString("southBoundLatitude", Number(box.MinY));
            writer.WriteElementString("northBoundLatitude", Number(box.MaxY));
            writer.WriteEndElement();
            return;
        }

        writer.WriteStartElement("LatLonBoundingBox");
        writer.WriteAttributeString("minx", Number(box.MinX));
        writer.WriteAttributeString("miny", Number(box.MinY));
        writer.WriteAttributeString("maxx", Number(box.MaxX));
        writer.WriteAttributeString("maxy", Number(box.MaxY));
        writer.WriteEndElement();
    }

    /// <summary>
    /// The extent in the layer's own CRS.
    /// </summary>
    /// <remarks>
    /// <b>In 1.3.0 the attributes follow the CRS's axis order despite being named
    /// minx and miny.</b> For <c>EPSG:4326</c> that makes <c>minx</c> a latitude,
    /// which reads like a bug and is the specification. Writing longitude there
    /// instead produces an extent every conforming client transposes.
    /// </remarks>
    private static void WriteBoundingBox(XmlWriter writer, WmsLayer layer, WmsVersion version)
    {
        if (layer.Extent is not { IsEmpty: false } box)
        {
            return;
        }

        bool swap = WmsNames.IsLatitudeFirst(version, layer.Srid);

        writer.WriteStartElement("BoundingBox");
        writer.WriteAttributeString(
            version == WmsVersion.V130 ? "CRS" : "SRS",
            $"EPSG:{layer.Srid.ToString(CultureInfo.InvariantCulture)}");

        writer.WriteAttributeString("minx", Number(swap ? box.MinY : box.MinX));
        writer.WriteAttributeString("miny", Number(swap ? box.MinX : box.MinY));
        writer.WriteAttributeString("maxx", Number(swap ? box.MaxY : box.MaxX));
        writer.WriteAttributeString("maxy", Number(swap ? box.MaxX : box.MaxY));
        writer.WriteEndElement();
    }

    /// <summary>The union of every layer's geographic extent, or null.</summary>
    private static Envelope? Whole(IReadOnlyList<WmsLayer> layers)
    {
        Envelope whole = Envelope.Empty;

        foreach (WmsLayer layer in layers)
        {
            if (layer.Geographic is { IsEmpty: false } box)
            {
                whole = whole.IsEmpty ? box : whole.Union(box);
            }
        }

        return whole.IsEmpty ? null : whole;
    }

    private static string Number(double value) =>
        value.ToString("0.##########", CultureInfo.InvariantCulture);
}
