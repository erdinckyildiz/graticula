using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>
/// The form pages behind each GeometryServer operation.
/// </summary>
/// <remarks>
/// <para>
/// <b>The service document listed its operations as text.</b> ArcGIS renders
/// each one as a link to a page you can fill in and run, and that is how anybody
/// learns what an operation takes — the owner asked why the capabilities were
/// not listed, and "they are, as a bulleted list nobody can click" was the
/// honest answer.
/// </para>
/// <para>
/// <b>The forms use GET, and that is a decision rather than a convenience.</b>
/// Every operation here is a pure function of its input: nothing is stored, no
/// row changes, and running one twice differs from running it once only in the
/// electricity. GET is the honest verb for that, and it is also the only verb
/// the browsing cookie authenticates
/// (<see cref="Authentication"/>) — a form posting on a cookie is exactly the
/// shape of request that rule exists to refuse. POST keeps working for clients
/// with a bearer token and a body too large for a URL.
/// </para>
/// </remarks>
internal static class GeometryPage
{
    /// <summary>A parameter, as it appears on the form.</summary>
    /// <param name="Name">The wire name, which is also the form field name.</param>
    /// <param name="Label">The label beside the box.</param>
    /// <param name="Hint">What it means, shown under the box.</param>
    /// <param name="Rows">Zero for a single line, more for a text area.</param>
    /// <param name="Default">Prefilled value, or null.</param>
    internal sealed record Parameter(
        string Name, string Label, string Hint, int Rows = 0, string? Default = null);

    /// <summary>An operation and what it takes.</summary>
    /// <param name="Name">The route segment.</param>
    /// <param name="Summary">One line, shown above the form.</param>
    /// <param name="Parameters">Its inputs, in the order they are asked for.</param>
    internal sealed record Operation(
        string Name, string Summary, IReadOnlyList<Parameter> Parameters);

    private const string GeometriesHint =
        "An ArcGIS geometry array: either a bare <code>[ ... ]</code> or the documented wrapper "
        + "<code>{\"geometryType\": \"esriGeometryPolygon\", \"geometries\": [ ... ]}</code>.";

    private static readonly Parameter Geometries =
        new("geometries", "Geometries", GeometriesHint, Rows: 7);

    private static readonly Parameter Sr =
        new("sr", "Spatial reference", "The well-known id the geometries are in — a bare number, "
            + "or <code>{\"wkid\": 3857}</code>.", Default: "4326");

    /// <summary>Every operation this surface answers, with its parameters.</summary>
    /// <remarks>
    /// <b>Kept beside the handlers rather than derived from them.</b> A reader
    /// pulled out of the parse code would be a second source of truth that drifts
    /// silently; this one drifts loudly, because the form's field names are the
    /// field names the handler reads and a rename breaks the round trip test.
    /// </remarks>
    internal static readonly IReadOnlyList<Operation> Operations =
    [
        new("project",
            "Moves geometries between coordinate reference systems. The response names the "
            + "engine that did it, because several transformation paths usually exist and they "
            + "differ by metres.",
            [
                new("inSR", "Input spatial reference",
                    "The well-known id the geometries are in.", Default: "4326"),
                new("outSR", "Output spatial reference",
                    "The well-known id to project to.", Default: "3857"),
                Geometries,
            ]),

        new("areasAndLengths",
            "Planar area and perimeter of each polygon, in the units of the spatial reference. "
            + "Not geodesic — see the note on the response.",
            [Sr, Geometries]),

        new("lengths",
            "Planar length of each geometry, in the units of the spatial reference.",
            [Sr, Geometries]),

        new("labelPoints",
            "A point guaranteed to fall inside each polygon, which is not the centroid — the "
            + "centroid of a crescent falls outside it.",
            [Sr, Geometries]),

        new("convexHull",
            "The smallest convex polygon containing every input geometry — one hull for the "
            + "whole set, which is what ArcGIS returns. Computed in this process, not in the "
            + "database: the geometry arrived in the request, so there is nothing to push down "
            + "to.",
            [Sr, Geometries]),

        new("densify",
            "Adds vertices so that no segment is longer than the given length. Every original "
            + "coordinate survives at its original value — densifying only adds.",
            [Sr, Geometries,
             new("maxSegmentLength", "Maximum segment length",
                 "The longest segment to leave alone, in the units of the spatial reference. "
                 + "Must be greater than zero.", Default: "1000")]),

        new("generalize",
            "Removes vertices that sit within a tolerance of the line replacing them "
            + "(Douglas–Peucker). This is not ArcGIS 'simplify', which repairs topology and "
            + "is not offered here.",
            [Sr, Geometries,
             new("maxDeviation", "Maximum deviation",
                 "How far a dropped vertex may be from the line replacing it, in the units of "
                 + "the spatial reference. Zero removes only exactly-collinear vertices.",
                 Default: "10")]),

        new("intersect",
            "The parts of each geometry in the list that fall inside the single geometry. Runs "
            + "in a worker process with a deadline.",
            [Sr, Geometries, Operand("intersected against")]),

        new("difference",
            "Each geometry in the list with the single geometry removed from it. Runs in a "
            + "worker process with a deadline.",
            [Sr, Geometries, Operand("subtracted from each")]),

        new("union",
            "One geometry covering all of them. Runs in a worker process with a deadline.",
            [Sr, Geometries]),

        new("toGeoCoordinateString",
            "Writes coordinates as grid or sexagesimal strings. Coordinates in a reference "
            + "other than 4326 are projected first by the datastore's PROJ, and the response "
            + "says what did it.",
            [Sr,
             new("coordinates", "Coordinates",
                 "A JSON array of <code>[x, y]</code> pairs, in the spatial reference above.",
                 Rows: 5, Default: "[[32.8597, 39.9334]]"),
             new("conversionType", "Notation",
                 "DD, DDM, DMS, UTM, MGRS or USNG. GARS and GEOREF are ArcGIS types this "
                 + "server does not write yet.", Default: "MGRS"),
             new("numOfDigits", "Digits",
                 "Digits per axis for the grid notations — five is one metre — or "
                 + "decimal places for the angular ones."),
             new("addSpaces", "Spaces",
                 "<code>false</code> to run the parts together, which is the usual MGRS form.",
                 Default: "true")]),

        new("fromGeoCoordinateString",
            "Reads grid or sexagesimal strings back into coordinates. A reference shorter than "
            + "ten digits names a square, and reads back to that square's centre.",
            [Sr,
             new("strings", "Strings",
                 "A JSON array of strings, all in the notation below.",
                 Rows: 5, Default: "[\"36TWF1234567890\"]"),
             new("conversionType", "Notation",
                 "DD, DDM, DMS, UTM, MGRS or USNG.", Default: "MGRS")]),

        new("cut",
            "The pieces the target splits into along the cutter. A cutter that misses returns "
            + "the target unchanged, as one piece. Runs in a worker process with a deadline.",
            [Sr,
             new("target", "Target", "The single ArcGIS geometry to cut.", Rows: 5),
             new("cutter", "Cutter",
                 "A single ArcGIS geometry, usually a polyline, to cut it along.", Rows: 5)]),

        new("buffer",
            "Everything within a distance of each geometry. A negative distance shrinks a "
            + "polygon and may empty it. Planar, in the units of the spatial reference — this "
            + "is not the geodesic buffer ArcGIS also offers.",
            [Sr, Geometries,
             new("distances", "Distance",
                 "One distance, in the units of the spatial reference. ArcGIS accepts a list "
                 + "and returns a ring per value; this server buffers once.", Default: "100")]),

        new("offset",
            "Each geometry's boundary moved sideways by a distance, as a line. The sign chooses "
            + "the side.",
            [Sr, Geometries,
             new("offsetDistance", "Offset distance",
                 "How far to move the curve, in the units of the spatial reference. Negative "
                 + "puts it on the other side.", Default: "10")]),

        new("simplify",
            "Makes each geometry valid — repairs self-intersections, closes rings, drops "
            + "zero-area slivers, fixes ring orientation. This is ArcGIS 'simplify' and it is "
            + "not vertex reduction; that is 'generalize', above.",
            [Sr, Geometries]),

        new("relation",
            "Which pairs out of two sets satisfy a topological relation. The answer is index "
            + "pairs into the two lists, and the comparison happens in one round trip rather "
            + "than one per pair.",
            [Sr,
             new("geometries1", "First geometries", GeometriesHint, Rows: 6),
             new("geometries2", "Second geometries", GeometriesHint, Rows: 6),
             new("relation", "Relation",
                 "esriGeometryRelationDisjoint, esriGeometryRelationIntersection, "
                 + "esriGeometryRelationWithin, esriGeometryRelationTouch, "
                 + "esriGeometryRelationCross or esriGeometryRelationOverlap. Esri's four "
                 + "refinements of these are refused rather than approximated.",
                 Default: "esriGeometryRelationIntersection"),
             new("relationParam", "DE-9IM pattern",
                 "A nine-character DE-9IM pattern, used instead of a named relation. "
                 + "<code>T********</code> is \"the interiors meet\".")]),

        new("distance",
            "The shortest planar distance between two geometries, zero when they touch or one "
            + "contains the other.",
            [Sr,
             new("geometry1", "First geometry", "A single ArcGIS geometry.", Rows: 5),
             new("geometry2", "Second geometry", "A single ArcGIS geometry.", Rows: 5)]),
    ];

    private static Parameter Operand(string role) =>
        new("geometry", "Geometry",
            $"A single ArcGIS geometry, {role} the list above.", Rows: 5);

    /// <summary>Finds an operation by name.</summary>
    /// <param name="name">The route segment.</param>
    /// <returns>The operation, or null if this surface does not offer it.</returns>
    internal static Operation? Find(string name) =>
        Operations.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// Whether this request wants the form rather than an answer.
    /// </summary>
    /// <remarks>
    /// <b>The trigger is the geometry, not the submit button.</b> A browser
    /// submits every field on the form including the ones it prefilled, so
    /// keying off "did anything arrive" would run the operation on an empty form
    /// — the same mistake the layer query page made and was corrected for. No
    /// operation here means anything without geometry, so its presence is
    /// unambiguous intent to run.
    /// </remarks>
    /// <param name="request">The request.</param>
    /// <returns>True to render the form.</returns>
    internal static bool WantsForm(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        foreach (string field in GeometryFields)
        {
            if (!string.IsNullOrWhiteSpace(request.Query[field]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Every field name a geometry can arrive under, on any operation.
    /// </summary>
    /// <remarks>
    /// <b>This list was two names and six operations arrived that use none of
    /// them.</b> ArcGIS spells the operands per operation — <c>target</c> and
    /// <c>cutter</c>, <c>geometry1</c> and <c>geometry2</c>, <c>geometries1</c>
    /// and <c>geometries2</c> — and with only "geometries" and "geometry"
    /// checked, submitting the cut form would have rendered the form back at the
    /// caller forever, with no error and nothing to indicate why.
    /// </remarks>
    private static readonly string[] GeometryFields =
        ["geometries", "geometry", "target", "cutter",
         "geometry1", "geometry2", "geometries1", "geometries2",
         "coordinates", "strings"];

    /// <summary>Renders the form for an operation.</summary>
    /// <param name="path">The request path, for the breadcrumb.</param>
    /// <param name="operation">The operation.</param>
    /// <param name="query">The current values, so a resubmission keeps them.</param>
    /// <param name="message">A message above the form, or null.</param>
    /// <returns>The page.</returns>
    internal static string Form(
        string path, Operation operation, IQueryCollection query, string? message = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(query);

        StringBuilder body = new();

        body.Append(CultureInfo.InvariantCulture,
            $"<h2>{RestDirectory.Encode(operation.Name)}</h2>");

        body.Append(CultureInfo.InvariantCulture,
            $"<p class=\"lede\">{operation.Summary}</p>");

        if (message is not null)
        {
            body.Append(CultureInfo.InvariantCulture,
                $"<p class=\"warn\">{RestDirectory.Encode(message)}</p>");
        }

        // GET, so the result has a URL somebody can keep, paste into a ticket, or
        // hand to curl. That is most of the value of a form page like this one.
        body.Append("<form method=\"get\" class=\"query\"><table class=\"form\">");

        foreach (Parameter parameter in operation.Parameters)
        {
            string current = query[parameter.Name].ToString();
            string value = current.Length > 0 ? current : parameter.Default ?? string.Empty;

            string control = parameter.Rows > 0
                ? $"<textarea name=\"{RestDirectory.Encode(parameter.Name)}\" rows=\""
                  + $"{parameter.Rows}\" cols=\"78\">{RestDirectory.Encode(value)}</textarea>"
                : $"<input type=\"text\" name=\"{RestDirectory.Encode(parameter.Name)}\" size=\"24\""
                  + $" value=\"{RestDirectory.Encode(value)}\">";

            body.Append(CultureInfo.InvariantCulture,
                $"<tr><th>{RestDirectory.Encode(parameter.Label)}:</th><td>{control}"
                + $"<div class=\"hint\">{parameter.Hint}</div></td></tr>");
        }

        string format = query["f"].ToString();

        body.Append(
            "<tr><th>Format:</th><td><select name=\"f\">"
            + $"<option value=\"html\"{Selected(format, "html", true)}>HTML</option>"
            + $"<option value=\"json\"{Selected(format, "json", false)}>JSON</option>"
            + "</select></td></tr>");

        body.Append("<tr><th></th><td><button type=\"submit\">"
            + operation.Name + "</button></td></tr>");

        body.Append("</table></form>");

        body.Append(
            "<p class=\"hint\">The same operation accepts a form-encoded POST with a "
            + "<code>Authorization: Bearer</code> header, which is what ArcGIS clients send and "
            + "what a body too large for a URL needs. This page uses GET because every operation "
            + "here is a pure function, and because the browsing cookie authenticates reads "
            + "only.</p>");

        return RestDirectory.Wrap(path, body.ToString());
    }

    private static string Selected(string current, string value, bool fallback) =>
        string.Equals(current, value, StringComparison.OrdinalIgnoreCase)
        || (current.Length == 0 && fallback)
            ? " selected"
            : string.Empty;
}
