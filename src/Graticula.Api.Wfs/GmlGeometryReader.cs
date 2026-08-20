using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using Graticula.Geometries;

namespace Graticula.Api.Wfs;

/// <summary>
/// Reads the GML 3.2 geometries a filter may carry.
/// </summary>
/// <remarks>
/// <para>
/// <b>A subset, and it is named rather than implied.</b> GML can describe arcs,
/// surfaces bounded by curves, solids and composites; this reads the shapes this
/// product stores — point, line, polygon and their collections — plus
/// <c>gml:Envelope</c>, which is what a BBOX is. Anything else is refused by name,
/// because a client whose arc was silently read as a straight line gets an answer
/// rather than an error.
/// </para>
/// <para>
/// <b>The axis-order rule is the same one the writer uses</b>
/// (<see cref="WfsNames.IsLatitudeFirst"/>): a filter geometry in
/// <c>urn:ogc:def:crs:EPSG::4326</c> arrives latitude first, and reading it
/// easting-first puts the client's map extent in the wrong hemisphere. The
/// symptom is zero features and no error, which is the same silent failure Q-96
/// recorded for tiles.
/// </para>
/// </remarks>
public static class GmlGeometryReader
{
    /// <summary>Reads a geometry element.</summary>
    /// <param name="element">The GML element.</param>
    /// <param name="defaultSrid">The layer's reference, used when the element names none.</param>
    /// <param name="depth">How deep the reader already is, counting the filter around it.</param>
    /// <param name="geometry">The shape.</param>
    /// <param name="srid">The reference it is in.</param>
    /// <param name="fault">Why it was refused.</param>
    /// <returns>Whether it read.</returns>
    public static bool TryRead(
        XElement element,
        int defaultSrid,
        int depth,
        out Geometry? geometry,
        out int srid,
        out WfsFault? fault)
    {
        ArgumentNullException.ThrowIfNull(element);

        geometry = null;
        fault = null;
        srid = defaultSrid;

        if (!TrySrid(element, defaultSrid, out srid, out fault))
        {
            return false;
        }

        bool latitudeFirst = WfsNames.IsLatitudeFirst(srid);

        return TryShape(element, latitudeFirst, depth, out geometry, out fault);
    }

    /// <summary>The EPSG code an <c>srsName</c> names.</summary>
    /// <remarks>
    /// <para>
    /// <b>Four spellings, because clients use all of them.</b>
    /// <c>urn:ogc:def:crs:EPSG::4326</c> is what WFS 2.0 asks for,
    /// <c>http://www.opengis.net/def/crs/EPSG/0/4326</c> is its URL twin,
    /// <c>EPSG:4326</c> is what everything older sends, and a bare number appears
    /// in hand-written requests. In each, the code is what follows the last
    /// separator.
    /// </para>
    /// <para>
    /// <b>It must say EPSG, and that requirement was added after a test found the
    /// alternative.</b> The first version took the trailing digits of whatever it
    /// was given — which reads <c>NAD83</c> as EPSG:83 and then compares a filter
    /// against data in a reference nobody named. A CRS this cannot identify is
    /// refused, because defaulting one is how a query silently answers a different
    /// question.
    /// </para>
    /// </remarks>
    /// <param name="text">The srsName.</param>
    /// <param name="srid">The code.</param>
    /// <returns>Whether it parsed.</returns>
    public static bool TrySrsName(string? text, out int srid)
    {
        srid = 0;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string trimmed = text.Trim();

        if (Code(trimmed, out srid))
        {
            return true;
        }

        if (trimmed.IndexOf("EPSG", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        int separator = trimmed.LastIndexOfAny([':', '/', '#']);

        return separator >= 0 && Code(trimmed[(separator + 1)..], out srid);

        static bool Code(string candidate, out int value) =>
            int.TryParse(candidate, NumberStyles.None, CultureInfo.InvariantCulture, out value)
            && value > 0;
    }

    private static bool TrySrid(XElement element, int fallback, out int srid, out WfsFault? fault)
    {
        fault = null;
        srid = fallback;

        string? name = (string?)element.Attribute("srsName");

        if (string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        if (TrySrsName(name, out srid))
        {
            return true;
        }

        srid = fallback;

        fault = WfsFault.Invalid(
            "filter",
            $"'{name}' is not a coordinate reference this server recognises. Use "
            + "urn:ogc:def:crs:EPSG::<code>.");

        return false;
    }

    private static bool TryShape(
        XElement element, bool latitudeFirst, int depth, out Geometry? geometry, out WfsFault? fault)
    {
        geometry = null;
        fault = null;

        // <b>The guard that was missing, and the crash it let through.</b> A
        // gml:MultiSurface may hold surfaceMembers that are themselves
        // MultiSurfaces, so this recurses with the document — and until an
        // independent reviewer sent one, nothing counted the levels. Three
        // thousand of them in a 223 KB unauthenticated POST killed the process:
        // a StackOverflowException in .NET cannot be caught, so there was no
        // refusal, no log line and no server. The document-size bounds held
        // perfectly and were an order of magnitude too generous to matter.
        //
        // <b>The budget is shared with the filter's, not separate.</b> FilterReader
        // counts fes elements and hands its depth in here; a filter that is thirty
        // levels of Not around one geometry is the same stack as a geometry thirty
        // levels deep, and two counters that each allow the maximum allow twice it.
        if (depth > SafeXml.MaximumDepth)
        {
            fault = WfsFault.Invalid(
                "filter",
                $"The filter's geometry nests more than {SafeXml.MaximumDepth} levels deep, "
                + "counting the predicates around it. The limit exists because reading it is "
                + "recursive and a deep enough document would exhaust the stack, which cannot be "
                + "caught.");

            return false;
        }

        if (element.Name.Namespace != XNamespace.Get(WfsNames.Gml))
        {
            fault = WfsFault.Invalid(
                "filter",
                $"'{element.Name.LocalName}' is not a GML 3.2 geometry. The filter's geometry "
                + $"must be in the {WfsNames.Gml} namespace.");

            return false;
        }

        switch (element.Name.LocalName)
        {
            case "Envelope":
                return TryEnvelope(element, latitudeFirst, out geometry, out fault);

            case "Point":
                return TryPoint(element, latitudeFirst, out geometry, out fault);

            case "LineString":
                return TryLine(element, latitudeFirst, out geometry, out fault);

            case "Polygon":
                return TryPolygon(element, latitudeFirst, out geometry, out fault);

            case "MultiPoint":
                return TryCollection(element, latitudeFirst, depth, "pointMember", "pointMembers",
                    parts => new MultiPoint([.. parts.Cast<Point>()]), out geometry, out fault);

            case "MultiCurve":
            case "MultiLineString":
                return TryCollection(element, latitudeFirst, depth, "curveMember", "curveMembers",
                    parts => new MultiLineString([.. parts.Cast<LineString>()]),
                    out geometry, out fault);

            case "MultiSurface":
            case "MultiPolygon":
                return TryCollection(element, latitudeFirst, depth, "surfaceMember", "surfaceMembers",
                    parts => new MultiPolygon([.. parts.Cast<Polygon>()]), out geometry, out fault);

            default:
                fault = WfsFault.Invalid(
                    "filter",
                    $"'gml:{element.Name.LocalName}' is not a geometry this server reads. It "
                    + "reads Envelope, Point, LineString, Polygon, MultiPoint, MultiCurve and "
                    + "MultiSurface.");

                return false;
        }
    }

    private static bool TryEnvelope(
        XElement element, bool latitudeFirst, out Geometry? geometry, out WfsFault? fault)
    {
        geometry = null;

        XNamespace gml = WfsNames.Gml;

        if (!TryNumbers(element.Element(gml + "lowerCorner")?.Value, out double[] lower)
            || !TryNumbers(element.Element(gml + "upperCorner")?.Value, out double[] upper)
            || lower.Length < 2
            || upper.Length < 2)
        {
            fault = WfsFault.Invalid(
                "filter",
                "A gml:Envelope needs a lowerCorner and an upperCorner, each with two numbers.");

            return false;
        }

        (double minX, double minY) = Order(lower[0], lower[1], latitudeFirst);
        (double maxX, double maxY) = Order(upper[0], upper[1], latitudeFirst);

        // A rectangle rather than an Envelope, because a spatial filter compares
        // geometries and the envelope type is a bounding box rather than a shape.
        // Written counter-clockwise and closed, which is what LinearRing requires.
        geometry = new Polygon(new LinearRing(XySequence.Wrap(
        [
            minX, minY,
            maxX, minY,
            maxX, maxY,
            minX, maxY,
            minX, minY,
        ])));

        fault = null;
        return true;
    }

    private static bool TryPoint(
        XElement element, bool latitudeFirst, out Geometry? geometry, out WfsFault? fault)
    {
        geometry = null;

        XNamespace gml = WfsNames.Gml;

        string? text = element.Element(gml + "pos")?.Value ?? element.Element(gml + "coordinates")?.Value;

        if (!TryNumbers(text, out double[] numbers) || numbers.Length < 2)
        {
            fault = WfsFault.Invalid("filter", "A gml:Point needs a gml:pos with two numbers.");
            return false;
        }

        (double x, double y) = Order(numbers[0], numbers[1], latitudeFirst);

        geometry = new Point(x, y);
        fault = null;
        return true;
    }

    private static bool TryLine(
        XElement element, bool latitudeFirst, out Geometry? geometry, out WfsFault? fault)
    {
        geometry = null;

        if (!TryCoordinates(element, latitudeFirst, out XySequence coordinates, out fault))
        {
            return false;
        }

        if (coordinates.Count < 2)
        {
            fault = WfsFault.Invalid("filter", "A gml:LineString needs at least two positions.");
            return false;
        }

        geometry = new LineString(coordinates);
        return true;
    }

    private static bool TryPolygon(
        XElement element, bool latitudeFirst, out Geometry? geometry, out WfsFault? fault)
    {
        geometry = null;
        fault = null;

        XNamespace gml = WfsNames.Gml;

        XElement? exterior = element.Element(gml + "exterior") ?? element.Element(gml + "outerBoundaryIs");

        if (exterior?.Element(gml + "LinearRing") is not { } shellElement)
        {
            fault = WfsFault.Invalid(
                "filter", "A gml:Polygon needs a gml:exterior holding a gml:LinearRing.");

            return false;
        }

        if (!TryRing(shellElement, latitudeFirst, out LinearRing? shell, out fault))
        {
            return false;
        }

        List<LinearRing> holes = [];

        foreach (XElement interior in element.Elements(gml + "interior")
            .Concat(element.Elements(gml + "innerBoundaryIs")))
        {
            if (interior.Element(gml + "LinearRing") is not { } holeElement)
            {
                continue;
            }

            if (!TryRing(holeElement, latitudeFirst, out LinearRing? hole, out fault))
            {
                return false;
            }

            holes.Add(hole!);
        }

        geometry = new Polygon(shell!, holes);
        return true;
    }

    private static bool TryRing(
        XElement ring, bool latitudeFirst, out LinearRing? result, out WfsFault? fault)
    {
        result = null;

        if (!TryCoordinates(ring, latitudeFirst, out XySequence coordinates, out fault))
        {
            return false;
        }

        if (coordinates.Count < LinearRing.MinimumCoordinates)
        {
            fault = WfsFault.Invalid(
                "filter",
                $"A gml:LinearRing needs at least {LinearRing.MinimumCoordinates} positions and "
                + $"has {coordinates.Count}.");

            return false;
        }

        try
        {
            result = new LinearRing(coordinates);
        }
        catch (ArgumentException e)
        {
            fault = WfsFault.Invalid("filter", $"The gml:LinearRing is not usable: {e.Message}");
            return false;
        }

        return true;
    }

    private static bool TryCollection(
        XElement element,
        bool latitudeFirst,
        int depth,
        string member,
        string members,
        Func<IReadOnlyList<Geometry>, Geometry> build,
        out Geometry? geometry,
        out WfsFault? fault)
    {
        geometry = null;
        fault = null;

        XNamespace gml = WfsNames.Gml;

        List<Geometry> parts = [];

        // Both spellings: one wrapper per part, or one wrapper holding them all.
        IEnumerable<XElement> candidates = element
            .Elements(gml + member)
            .SelectMany(m => m.Elements())
            .Concat(element.Elements(gml + members).SelectMany(m => m.Elements()));

        foreach (XElement child in candidates)
        {
            if (!TryShape(child, latitudeFirst, depth + 1, out Geometry? part, out fault))
            {
                return false;
            }

            parts.Add(part!);
        }

        if (parts.Count == 0)
        {
            fault = WfsFault.Invalid(
                "filter", $"A gml:{element.Name.LocalName} has no members.");

            return false;
        }

        try
        {
            geometry = build(parts);
        }
        catch (InvalidCastException)
        {
            fault = WfsFault.Invalid(
                "filter",
                $"A gml:{element.Name.LocalName} holds a member of the wrong kind. Its parts must "
                + "all be the shape its name says.");

            return false;
        }

        return true;
    }

    private static bool TryCoordinates(
        XElement element, bool latitudeFirst, out XySequence coordinates, out WfsFault? fault)
    {
        coordinates = XySequence.Empty;
        fault = null;

        XNamespace gml = WfsNames.Gml;

        string? text = element.Element(gml + "posList")?.Value
            ?? element.Element(gml + "coordinates")?.Value;

        List<double> flat = [];

        if (text is not null)
        {
            if (!TryNumbers(text, out double[] numbers))
            {
                fault = WfsFault.Invalid(
                    "filter", "A gml:posList must hold numbers separated by whitespace.");

                return false;
            }

            flat.AddRange(numbers);
        }
        else
        {
            foreach (XElement pos in element.Elements(gml + "pos"))
            {
                if (!TryNumbers(pos.Value, out double[] one) || one.Length < 2)
                {
                    fault = WfsFault.Invalid("filter", "A gml:pos must hold two numbers.");
                    return false;
                }

                flat.Add(one[0]);
                flat.Add(one[1]);
            }
        }

        if (flat.Count == 0 || flat.Count % 2 != 0)
        {
            fault = WfsFault.Invalid(
                "filter",
                $"A coordinate list must hold an even number of values and holds {flat.Count}. "
                + "This server reads two dimensions; a posList with srsDimension=\"3\" is not "
                + "supported.");

            return false;
        }

        double[] interleaved = new double[flat.Count];

        for (int i = 0; i < flat.Count; i += 2)
        {
            (double x, double y) = Order(flat[i], flat[i + 1], latitudeFirst);
            interleaved[i] = x;
            interleaved[i + 1] = y;
        }

        coordinates = XySequence.Wrap(interleaved);
        return true;
    }

    private static (double X, double Y) Order(double first, double second, bool latitudeFirst) =>
        latitudeFirst ? (second, first) : (first, second);

    private static bool TryNumbers(string? text, out double[] numbers)
    {
        numbers = [];

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string[] parts = text.Split(
            [' ', '\t', '\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries);

        double[] parsed = new double[parts.Length];

        for (int i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(
                    parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out parsed[i]))
            {
                return false;
            }
        }

        numbers = parsed;
        return parsed.Length > 0;
    }
}
