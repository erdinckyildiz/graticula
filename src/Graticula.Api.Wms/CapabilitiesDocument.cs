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
    /// <summary>
    /// The one reference the root layer states, and so the one every layer inherits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It was a fixed three — <c>EPSG:4326</c>, <c>EPSG:3857</c>, <c>CRS:84</c> — written
    /// on the root for every deployment, and that stopped being true on 2026-09-09.</b> The
    /// owner: *"wms ve wfs map'in projeksiyonunda yayınlanacak"*, and separately, of a longer
    /// list, *"listelensin istemiyorum"*. A service now names the reference it is published in
    /// (ADR-057 §5c) and each named layer states that one; a fixed three at the root offered
    /// two more references that had nothing to do with what any service chose.
    /// </para>
    /// <para>
    /// <b>Why one survives rather than none, which is a judgement rather than a reading.</b>
    /// 1.3.0 §7.3.3.1 makes <c>CRS</c> a required <c>GetMap</c> parameter, so on this face
    /// there is no such thing as a default: the advertised set *is* the publication, and a
    /// document whose only reference is a national grid turns away every client that cannot
    /// look one up. This server also states every layer's <c>EX_GeographicBoundingBox</c> in
    /// WGS 84 and states the world in <c>CRS:84</c> for a layer with no extent, so refusing to
    /// list it would leave the document stating boxes in a reference it claims not to support.
    /// §7.2.4.6.7 requires *at least* one per layer and forbids no extra, and one inherited
    /// entry is not the list that was declined.
    /// </para>
    /// <para>
    /// <b>1.1.1 gets <c>EPSG:4326</c> because it has no <c>CRS:84</c>.</b> The name arrived
    /// with 1.3.0, to undo the axis rule 1.3.0 introduced; in 1.1.1 <c>EPSG:4326</c> is already
    /// longitude first, so it is the same reference under the spelling that version has.
    /// </para>
    /// </remarks>
    /// <param name="version">Which version is being written.</param>
    /// <returns>The code.</returns>
    public static string Universal(WmsVersion version) =>
        version == WmsVersion.V130 ? "CRS:84" : "EPSG:4326";

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

        writer.WriteElementString("CRS", Universal(WmsVersion.V130));

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

        writer.WriteElementString("SRS", Universal(WmsVersion.V111));

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
            //
            // <b>[D-03](../../docs/architecture-debt.md) took the fifth one out.</b> It was
            // `PostGIS`, which fails this list's own test in a way the four survivors do
            // not: it is not something a client can do here, it is what sits behind the
            // server — the provider type [security.md](../../docs/security.md) §5 keeps for
            // an authenticated administrator. `GetLegendGraphic` replaces it and is a
            // request this server actually answers.
            ["WMS", "GetMap", "GetFeatureInfo", "GetLegendGraphic", "vector"]);

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

        // <b>Two clauses when the two references differ, one when they do not.</b> *Held in*
        // and *published in* are different facts (ADR-057 §5c) and the sentence a person reads
        // in a layer picker is the wrong place to collapse them — an operator looking at
        // metres in a document that says degrees has nothing else on this face to explain it.
        string text =
            $"{geometry} held in EPSG:{layer.Srid.ToString(CultureInfo.InvariantCulture)}"
            + (layer.IsReprojected
                ? ", published in EPSG:"
                    + layer.PublishedSrid.ToString(CultureInfo.InvariantCulture) + ", "
                : ", ")
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

        WriteLayerReferences(writer, layer, WmsVersion.V130);

        WriteGeographicBox(writer, layer.Geographic, WmsVersion.V130, required: true);
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

        WriteLayerReferences(writer, layer, WmsVersion.V111);

        WriteGeographicBox(writer, layer.Geographic, WmsVersion.V111, required: true);
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
    /// The references this layer states for itself, over the one it inherits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The first is what the service publishes this layer in</b> — ADR-057 §5c, and it used
    /// to be the table's own code because nothing on this face read the service. For every
    /// service that has named nothing they are the same number, which is why the difference was
    /// invisible until one did.
    /// </para>
    /// <para>
    /// <b>The second is written only when the layer's own <c>BoundingBox</c> is in a different
    /// reference, and it is not a second offer.</b> That box is stated in the table's metres —
    /// <c>EmptyLayerStillHasABoundingBoxTests</c> holds that decision, and it is the right one:
    /// replacing an extent with the world makes every document conformant and every extent
    /// useless. A box stated in a reference the layer does not list is a box a client can read
    /// and may not ask in, which is the document disagreeing with itself rather than with the
    /// server. So the code appears because the document already uses it, and a service that has
    /// chosen nothing still states exactly one.
    /// </para>
    /// <para>
    /// <b>Neither of them is <c>CRS</c> in 1.1.1</b>, where the element is <c>SRS</c> and the
    /// axis rule that makes 1.3.0 delicate does not exist.
    /// </para>
    /// </remarks>
    private static void WriteLayerReferences(
        XmlWriter writer, WmsLayer layer, WmsVersion version)
    {
        string element = version == WmsVersion.V130 ? "CRS" : "SRS";

        writer.WriteElementString(
            element, $"EPSG:{layer.PublishedSrid.ToString(CultureInfo.InvariantCulture)}");

        if (layer.IsReprojected && layer.Extent is { IsEmpty: false })
        {
            writer.WriteElementString(
                element, $"EPSG:{layer.Srid.ToString(CultureInfo.InvariantCulture)}");
        }
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
        writer.WriteAttributeString(
            "width",
            layer.LegendSize.Width.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString(
            "height",
            layer.LegendSize.Height.ToString(CultureInfo.InvariantCulture));
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
    private static void WriteGeographicBox(
        XmlWriter writer, Envelope? extent, WmsVersion version, bool required = false)
    {
        /*
          <b>A named layer with nothing in it still owes this element — WMS 1.3.0
          §7.2.4.6.6, and 1.1.1 §7.1.4.5.6 for its own.</b> *Every named Layer shall have
          exactly one EX_GeographicBoundingBox element that is either stated explicitly or
          inherited from a parent Layer.* An empty layer has no extent to state and this
          server's root layer states none either, so there was nothing to inherit and the
          element was simply missing.

          <b>The whole world is the honest value, and it is worth saying why it is not a
          lie.</b> It does not claim the data spans the earth — an empty layer has no data
          to span anything. It says *this is not constrained*, which is exactly what is
          known, and it is how a bounding box says *unknown*: the alternative that suggests
          itself, a zero-area box at the origin, is a false pinpoint off West Africa.

          <b>Found by the WMS 1.3 CITE suite on 2026-08-26</b>, which failed *every named
          layer in the capabilities document has at least one BoundingBox element*. Two of
          this deployment's fourteen layers are empty and neither carried either element.
        */
        if (extent is not { IsEmpty: false } box)
        {
            if (!required)
            {
                return;
            }

            box = new Envelope(-180, -90, 180, 90);
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
        /*
          <b>Same requirement, other element — 1.3.0 §7.2.4.6.8.</b> *Every named Layer
          shall have at least one BoundingBox element that is either stated explicitly or
          inherited from a parent Layer.*

          <b>Written in CRS:84 rather than in the layer's own reference</b>, and that is
          the point rather than a shortcut. The world in the layer's own CRS would need
          that CRS's own domain — which this server cannot look up reliably
          ([Q-123](../../docs/open-questions.md) measured why) — while CRS:84 is
          longitude-first by definition, is inherited by every layer from the root, and
          needs no lookup and no axis decision. A `BoundingBox` need not be in the layer's
          native reference; it must be in one the layer supports.

          <b>1.1.1 has no CRS:84</b>, so it gets `EPSG:4326`, which in 1.1.1 is
          longitude-first anyway — the axis rule that makes this delicate arrived with
          1.3.0.
        */
        if (layer.Extent is not { IsEmpty: false } box)
        {
            writer.WriteStartElement("BoundingBox");

            writer.WriteAttributeString(
                version == WmsVersion.V130 ? "CRS" : "SRS",
                version == WmsVersion.V130 ? "CRS:84" : "EPSG:4326");

            writer.WriteAttributeString("minx", Number(-180));
            writer.WriteAttributeString("miny", Number(-90));
            writer.WriteAttributeString("maxx", Number(180));
            writer.WriteAttributeString("maxy", Number(90));
            writer.WriteEndElement();

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
