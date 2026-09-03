using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Graticula.Geometries;

namespace Graticula.Cartography;

/// <summary>
/// Reads and writes the subset of Esri's CIM that this server understands.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-052](../../../docs/adr/ADR-052-the-canonical-symbology-document-is-cim.md), owner
/// decision 2026-09-03: the canonical symbology document is a CIM renderer.</b> The property
/// names and shapes here were read off the published specification at
/// <c>github.com/Esri/cim-spec</c> — <c>docs/v3/CIMRenderers.md</c>,
/// <c>docs/v3/CIMSymbols.md</c>, <c>docs/v3/CIMColor.md</c> — on that date. The specification is
/// the citation; no product is.
/// </para>
/// <para>
/// <b>The document is not deserialised into a closed model, and that is the point.</b> A CIM
/// renderer this server does not fully understand is still stored verbatim, because ADR-052's
/// whole argument is that a canonical model poorer than what it is handed loses information
/// permanently. So the reader <i>projects</i>: it pulls out the parts that can be drawn and
/// leaves the document alone. What it could not project is reported, never dropped.
/// </para>
/// <para>
/// <b>Alpha is 0–100 and red, green and blue are 0–255.</b> The specification documents neither
/// range — it says only that alpha is last — so this comes from its own worked examples, which
/// write <c>[0, 122, 194, 100]</c> for an opaque blue. Esri's REST <c>drawingInfo</c> uses 0–255
/// for all four, so every conversion between the two rescales the fourth. Getting that backwards
/// makes every colour either fully opaque or very nearly invisible, with nothing in either
/// document to say which was meant. ADR-052 condition 1.
/// </para>
/// </remarks>
public static class Cim
{
    /// <summary>The renderer types this server reads.</summary>
    public const string Simple = "CIMSimpleRenderer";

    /// <summary>One symbol per distinct value of a field.</summary>
    public const string UniqueValue = "CIMUniqueValueRenderer";

    /// <summary>One symbol per range of a number.</summary>
    public const string ClassBreaks = "CIMClassBreaksRenderer";

    /// <summary>Whether a document is CIM, by its own discriminator.</summary>
    /// <remarks>
    /// <b>Asked of the value, not of the caller.</b> A CIM object names its own type in
    /// <c>type</c>, a MapLibre style has <c>layers</c> and an Esri <c>drawingInfo</c> has
    /// <c>renderer</c>; none carries another's key, so a paste never has to be declared.
    /// </remarks>
    /// <param name="body">The parsed document.</param>
    /// <returns>True when its <c>type</c> names a CIM renderer.</returns>
    public static bool IsRenderer(JsonObject body)
    {
        ArgumentNullException.ThrowIfNull(body);

        string kind = Text(body["type"]) ?? string.Empty;

        return kind.StartsWith("CIM", StringComparison.Ordinal);
    }

    /// <summary>
    /// Projects a CIM renderer onto what this server can draw.
    /// </summary>
    /// <param name="body">The stored renderer.</param>
    /// <param name="geometry">What the layer is made of.</param>
    /// <returns>The projection, and one sentence per thing it could not carry.</returns>
    public static CimProjection Project(JsonObject body, GeometryKind geometry)
    {
        ArgumentNullException.ThrowIfNull(body);

        List<string> notDrawn = [];
        string kind = Text(body["type"]) ?? string.Empty;

        return kind switch
        {
            Simple => ProjectSimple(body, geometry, notDrawn),
            UniqueValue => ProjectUniqueValue(body, geometry, notDrawn),
            ClassBreaks => ProjectClassBreaks(body, geometry, notDrawn),
            _ => throw new SymbologyException(
                $"'{kind}' is not a renderer this server reads. It reads `{Simple}`, "
                + $"`{UniqueValue}` and `{ClassBreaks}`. A renderer it cannot read is refused "
                + "rather than stored, because a stored document nothing can draw is a layer "
                + "that looks styled and is not."),
        };
    }

    /// <summary>One symbol for every feature.</summary>
    /// <param name="body">The renderer.</param>
    /// <param name="geometry">What the layer is made of.</param>
    /// <param name="notDrawn">Collects what could not be carried.</param>
    /// <returns>The projection.</returns>
    private static CimProjection ProjectSimple(
        JsonObject body, GeometryKind geometry, List<string> notDrawn)
    {
        CimSymbol symbol = ReadSymbol(body["symbol"], geometry, "the renderer's symbol", notDrawn);

        return new CimProjection(
            Simple,
            Field: null,
            [new CimClass(Values: [], UpperBound: null, Text(body["label"]) ?? string.Empty, symbol)],
            Default: null,
            notDrawn);
    }

    /// <summary>One symbol per distinct value.</summary>
    /// <param name="body">The renderer.</param>
    /// <param name="geometry">What the layer is made of.</param>
    /// <param name="notDrawn">Collects what could not be carried.</param>
    /// <returns>The projection.</returns>
    private static CimProjection ProjectUniqueValue(
        JsonObject body, GeometryKind geometry, List<string> notDrawn)
    {
        // <b>`fields` is an array and this server classifies by one.</b> Esri allows three,
        // combined; saying so is better than drawing by the first and letting somebody find out
        // from a map that two thirds of their distinction vanished.
        List<string> fields = (body["fields"] as JsonArray ?? [])
            .Select(f => Text(f))
            .Where(f => !string.IsNullOrEmpty(f))
            .Select(f => f!)
            .ToList();

        if (fields.Count == 0)
        {
            throw new SymbologyException(
                $"A `{UniqueValue}` names no field in `fields`, so there is nothing to classify "
                + "by.");
        }

        if (fields.Count > 1)
        {
            notDrawn.Add(
                $"The renderer classifies by {fields.Count} fields combined "
                + $"({string.Join(", ", fields)}); this server classifies by one and uses "
                + $"`{fields[0]}`. Features whose values differ only in the other fields are "
                + "drawn the same.");
        }

        List<CimClass> classes = [];

        foreach (JsonObject group in Objects(body["groups"]))
        {
            foreach (JsonObject one in Objects(group["classes"]))
            {
                if (one["visible"] is JsonValue visible
                    && visible.TryGetValue(out bool shown)
                    && !shown)
                {
                    notDrawn.Add(
                        $"The class '{Text(one["label"]) ?? "(unnamed)"}' is marked not visible. "
                        + "This server draws every feature it is given, so those features are "
                        + "drawn with the class's own symbol rather than hidden.");
                }

                // <b>One value per class, and the first of a multi-field tuple.</b> `values` is
                // a list of `CIMUniqueValue`, each with a `fieldValues` array as long as
                // `fields`.
                List<string> values = [];

                foreach (JsonObject value in Objects(one["values"]))
                {
                    if ((value["fieldValues"] as JsonArray)?.FirstOrDefault() is { } first
                        && Text(first) is { } text)
                    {
                        values.Add(text);
                    }
                }

                classes.Add(new CimClass(
                    values,
                    UpperBound: null,
                    Text(one["label"]) ?? string.Join(", ", values),
                    ReadSymbol(one["symbol"], geometry, "a unique-value class", notDrawn)));
            }
        }

        if (classes.Count == 0)
        {
            throw new SymbologyException(
                $"A `{UniqueValue}` has no classes in any of its `groups`, so it draws nothing.");
        }

        return new CimProjection(
            UniqueValue,
            fields[0],
            classes,
            Fallback(body, geometry, notDrawn),
            notDrawn);
    }

    /// <summary>One symbol per range of a number.</summary>
    /// <param name="body">The renderer.</param>
    /// <param name="geometry">What the layer is made of.</param>
    /// <param name="notDrawn">Collects what could not be carried.</param>
    /// <returns>The projection.</returns>
    private static CimProjection ProjectClassBreaks(
        JsonObject body, GeometryKind geometry, List<string> notDrawn)
    {
        if (Text(body["field"]) is not { Length: > 0 } field)
        {
            throw new SymbologyException(
                $"A `{ClassBreaks}` names no `field`, so there is nothing to classify by.");
        }

        List<CimClass> classes = [];

        foreach (JsonObject one in Objects(body["breaks"]))
        {
            if (one["upperBound"] is not JsonValue bound
                || !bound.TryGetValue(out double upper))
            {
                throw new SymbologyException(
                    "A class break has no numeric `upperBound`, so the range it stands for is "
                    + "not defined.");
            }

            classes.Add(new CimClass(
                Values: [],
                upper,
                Text(one["label"]) ?? upper.ToString(CultureInfo.InvariantCulture),
                ReadSymbol(one["symbol"], geometry, "a class break", notDrawn)));
        }

        if (classes.Count == 0)
        {
            throw new SymbologyException(
                $"A `{ClassBreaks}` has no `breaks`, so it draws nothing.");
        }

        // <b>Sorted, because `step` needs ascending stops and CIM does not promise them.</b>
        // `showInAscendingOrder` is about the legend, not about the data.
        classes.Sort((a, b) => (a.UpperBound ?? 0).CompareTo(b.UpperBound ?? 0));

        return new CimProjection(
            ClassBreaks, field, classes, Fallback(body, geometry, notDrawn), notDrawn);
    }

    /// <summary>The symbol for features no class matches, when the renderer offers one.</summary>
    /// <param name="body">The renderer.</param>
    /// <param name="geometry">What the layer is made of.</param>
    /// <param name="notDrawn">Collects what could not be carried.</param>
    /// <returns>The symbol, or null.</returns>
    private static CimSymbol? Fallback(
        JsonObject body, GeometryKind geometry, List<string> notDrawn)
    {
        // <b>`useDefaultSymbol` decides, and a missing flag means the symbol is used.</b> A
        // renderer that carries a default symbol and never draws it is the rarer intent.
        if (body["useDefaultSymbol"] is JsonValue flag
            && flag.TryGetValue(out bool wanted)
            && !wanted)
        {
            return null;
        }

        return body["defaultSymbol"] is null
            ? null
            : ReadSymbol(body["defaultSymbol"], geometry, "the default symbol", notDrawn);
    }

    /// <summary>
    /// Reads a <c>CIMSymbolReference</c> down to the layers this server paints with.
    /// </summary>
    /// <param name="node">The reference, or a bare symbol.</param>
    /// <param name="geometry">What the layer is made of.</param>
    /// <param name="where">Where this symbol sits, for a message that can be acted on.</param>
    /// <param name="notDrawn">Collects what could not be carried.</param>
    /// <returns>The symbol.</returns>
    private static CimSymbol ReadSymbol(
        JsonNode? node, GeometryKind geometry, string where, List<string> notDrawn)
    {
        if (node is not JsonObject reference)
        {
            throw new SymbologyException(
                $"There is no symbol at {where}, so the features it stands for would be drawn "
                + "with nothing.");
        }

        // <b>A reference wraps a symbol, and a bare symbol is accepted too.</b> Esri writes the
        // reference; a person writing one by hand usually does not, and refusing that would be a
        // rule about ceremony rather than about meaning.
        JsonObject symbol = reference["symbol"] as JsonObject ?? reference;

        if (reference["primitiveOverrides"] is JsonArray { Count: > 0 } overrides)
        {
            notDrawn.Add(
                $"The symbol at {where} carries {overrides.Count} primitive override(s), which "
                + "vary a symbol layer's properties per feature by an Arcade expression. This "
                + "server does not evaluate Arcade, so the symbol is drawn as authored.");
        }

        List<CimPaint> paints = [];
        double[]? dashes = null;

        foreach (JsonObject effect in Objects(symbol["effects"]))
        {
            if (Text(effect["type"]) == "CIMGeometricEffectDashes")
            {
                dashes = Numbers(effect["dashTemplate"]);
                continue;
            }

            notDrawn.Add(
                $"The symbol at {where} has a `{Text(effect["type"]) ?? "(unnamed)"}` geometric "
                + "effect. This server draws only the dash effect, so the geometry is drawn "
                + "unmodified.");
        }

        foreach (JsonObject part in Objects(symbol["symbolLayers"]))
        {
            if (part["enable"] is JsonValue enabled
                && enabled.TryGetValue(out bool on)
                && !on)
            {
                continue;
            }

            switch (Text(part["type"]))
            {
                case "CIMSolidFill":
                    paints.Add(new CimFill(Colour(part["color"], where, notDrawn)));
                    break;

                case "CIMSolidStroke":
                    paints.Add(new CimStroke(
                        Colour(part["color"], where, notDrawn),
                        Number(part["width"]) ?? 1,
                        Dashes(part) ?? dashes,
                        Text(part["capStyle"]),
                        Text(part["joinStyle"])));
                    break;

                case "CIMVectorMarker":
                    paints.Add(new CimMarker(
                        Number(part["size"]) ?? 8,
                        MarkerColour(part, where, notDrawn)));
                    break;

                default:
                    notDrawn.Add(
                        $"The symbol at {where} has a `{Text(part["type"]) ?? "(unnamed)"}` "
                        + "layer. This server paints solid fills, solid strokes and vector "
                        + "markers, so that layer is not drawn. It is kept in the stored "
                        + "document.");
                    break;
            }
        }

        // <b>One list, in painting order, reversed on the way in.</b> CIM draws
        // `symbolLayers[0]` on top; MapLibre and this renderer draw the first layer first, which
        // is underneath. Keeping CIM's order would put a casing over the fill it is meant to sit
        // under, which is the one mistake that makes a road look like a ditch.
        //
        // <b>And it is one list rather than a list of fills beside a list of strokes.</b> Two
        // lists lose the order *between* them, so a fill authored over a stroke would be drawn
        // under it and there would be nothing anywhere to say so.
        paints.Reverse();

        if (paints.Count == 0)
        {
            throw new SymbologyException(
                $"The symbol at {where} has no layer this server can paint with. It reads "
                + "`CIMSolidFill`, `CIMSolidStroke` and `CIMVectorMarker`.");
        }

        return new CimSymbol(geometry, paints);
    }

    /// <summary>A stroke's own dash template, when it carries one.</summary>
    /// <param name="stroke">The stroke.</param>
    /// <returns>The template, or null.</returns>
    private static double[]? Dashes(JsonObject stroke)
    {
        foreach (JsonObject effect in Objects(stroke["effects"]))
        {
            if (Text(effect["type"]) == "CIMGeometricEffectDashes")
            {
                return Numbers(effect["dashTemplate"]);
            }
        }

        return null;
    }

    /// <summary>The colour a vector marker's graphic is filled with.</summary>
    /// <param name="marker">The marker layer.</param>
    /// <param name="where">Where this symbol sits.</param>
    /// <param name="notDrawn">Collects what could not be carried.</param>
    /// <returns>The colour.</returns>
    private static Rgba MarkerColour(JsonObject marker, string where, List<string> notDrawn)
    {
        // <b>A vector marker is a little drawing, and this server draws a disc.</b> The
        // graphic's own geometry is not traced; its fill colour and the marker's size are what
        // survive, and the reader says so rather than pretending the shape came through.
        foreach (JsonObject graphic in Objects(marker["markerGraphics"]))
        {
            if (graphic["symbol"] is not JsonObject inner)
            {
                continue;
            }

            foreach (JsonObject part in Objects(inner["symbolLayers"]))
            {
                if (Text(part["type"]) == "CIMSolidFill")
                {
                    return Colour(part["color"], where, notDrawn);
                }
            }
        }

        notDrawn.Add(
            $"The vector marker at {where} has no solid fill in its graphics, so its colour "
            + "could not be read and it is drawn in grey.");

        return new Rgba(136, 136, 136, 255);
    }

    /// <summary>A colour, from <c>CIMRGBColor</c>.</summary>
    /// <param name="node">The colour.</param>
    /// <param name="where">Where it sits.</param>
    /// <param name="notDrawn">Collects what could not be carried.</param>
    /// <returns>The colour, opaque grey when it cannot be read.</returns>
    private static Rgba Colour(JsonNode? node, string where, List<string> notDrawn)
    {
        if (node is not JsonObject colour)
        {
            return new Rgba(136, 136, 136, 255);
        }

        string model = Text(colour["type"]) ?? "CIMRGBColor";

        if (model != "CIMRGBColor")
        {
            notDrawn.Add(
                $"The colour at {where} is a `{model}`. This server reads `CIMRGBColor`, so that "
                + "colour is drawn in grey. Converting between colour models needs an ICC "
                + "profile, and guessing one produces a colour nobody chose.");

            return new Rgba(136, 136, 136, 255);
        }

        double[] values = Numbers(colour["values"]) ?? [];

        if (values.Length < 3)
        {
            notDrawn.Add(
                $"The colour at {where} has fewer than three channels, so it is drawn in grey.");

            return new Rgba(136, 136, 136, 255);
        }

        return new Rgba(
            Channel(values[0]),
            Channel(values[1]),
            Channel(values[2]),
            values.Length > 3 ? Opacity(values[3]) : (byte)255);
    }

    /// <summary>A 0–255 channel, clamped and rounded.</summary>
    /// <param name="value">What the document said.</param>
    /// <returns>The byte.</returns>
    private static byte Channel(double value) =>
        (byte)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);

    /// <summary>CIM's 0–100 alpha as a 0–255 one.</summary>
    /// <remarks>
    /// <b>The rescale ADR-052 §3.3 is about.</b> A document that meant *opaque* says 100 here
    /// and 255 in Esri's REST vocabulary; copying rather than scaling makes it 39% opaque, which
    /// looks like a rendering bug rather than like a units mistake.
    /// </remarks>
    /// <param name="percent">Alpha, 0–100.</param>
    /// <returns>Alpha, 0–255.</returns>
    public static byte Opacity(double percent) =>
        (byte)Math.Clamp(
            Math.Round(percent * 255 / 100, MidpointRounding.AwayFromZero), 0, 255);

    /// <summary>A 0–255 alpha as CIM's 0–100 one.</summary>
    /// <param name="alpha">Alpha, 0–255.</param>
    /// <returns>Alpha, 0–100.</returns>
    public static double Percent(byte alpha) =>
        Math.Round(alpha * 100.0 / 255, 2, MidpointRounding.AwayFromZero);

    /// <summary>Writes a <c>CIMRGBColor</c>.</summary>
    /// <param name="colour">The colour.</param>
    /// <returns>The node.</returns>
    public static JsonObject Colour(Rgba colour) =>
        new()
        {
            ["type"] = "CIMRGBColor",
            ["values"] = new JsonArray(
                (int)colour.R, (int)colour.G, (int)colour.B, Percent(colour.A)),
        };

    /// <summary>The objects of an array property, skipping anything that is not one.</summary>
    /// <param name="node">The property.</param>
    /// <returns>Its objects.</returns>
    private static IEnumerable<JsonObject> Objects(JsonNode? node) =>
        node is JsonArray array ? array.OfType<JsonObject>() : [];

    /// <summary>A string property, or null.</summary>
    /// <param name="node">The property.</param>
    /// <returns>Its text.</returns>
    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    /// <summary>A number property, or null.</summary>
    /// <param name="node">The property.</param>
    /// <returns>Its value.</returns>
    private static double? Number(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        // <b>Asked in several types, because a `JsonValue` remembers the one it was made
        // with.</b> A number that came from `JsonNode.Parse` is backed by a `JsonElement` and
        // answers to `double`; one this server built with `JsonValue.Create(12)` is a
        // `JsonValue<int>` and does not. Measured 2026-09-03: every colour survived a document
        // read from text and every colour came back opaque grey through a document built in
        // memory, which is exactly the round trip a `PUT` performs.
        if (value.TryGetValue(out double asDouble))
        {
            return asDouble;
        }

        if (value.TryGetValue(out int asInt))
        {
            return asInt;
        }

        if (value.TryGetValue(out long asLong))
        {
            return asLong;
        }

        if (value.TryGetValue(out byte asByte))
        {
            return asByte;
        }

        return value.TryGetValue(out decimal asDecimal) ? (double)asDecimal : null;
    }

    /// <summary>An array of numbers, or null.</summary>
    /// <param name="node">The property.</param>
    /// <returns>Its values.</returns>
    private static double[]? Numbers(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return null;
        }

        List<double> found = [];

        foreach (JsonNode? item in array)
        {
            if (Number(item) is { } number)
            {
                found.Add(number);
            }
        }

        return found.Count == 0 ? null : [.. found];
    }
}

/// <summary>What a CIM renderer says, in the terms this server draws in.</summary>
/// <param name="Kind">Which of the three renderers it is.</param>
/// <param name="Field">The field it classifies by, or null for a simple renderer.</param>
/// <param name="Classes">One per symbol, in the order the renderer gives them.</param>
/// <param name="Default">What features no class matches are drawn with, or null.</param>
/// <param name="NotDrawn">One sentence per thing the document holds and this server does not
/// draw.</param>
public sealed record CimProjection(
    string Kind,
    string? Field,
    IReadOnlyList<CimClass> Classes,
    CimSymbol? Default,
    IReadOnlyList<string> NotDrawn);

/// <summary>One symbol and what it stands for.</summary>
/// <param name="Values">The field values this class matches, for a unique-value renderer.</param>
/// <param name="UpperBound">The top of this class's range, for class breaks.</param>
/// <param name="Label">What a legend calls it.</param>
/// <param name="Symbol">What it is drawn with.</param>
public sealed record CimClass(
    IReadOnlyList<string> Values, double? UpperBound, string Label, CimSymbol Symbol);

/// <summary>A symbol, as a stack of the layers this server paints with.</summary>
/// <param name="Geometry">What the layer it draws is made of.</param>
/// <param name="Paints">Bottom first, so a casing precedes the line that sits on it.</param>
public sealed record CimSymbol(GeometryKind Geometry, IReadOnlyList<CimPaint> Paints);

/// <summary>One layer of a symbol.</summary>
public abstract record CimPaint;

/// <summary>A solid fill.</summary>
/// <param name="Colour">What it paints.</param>
public sealed record CimFill(Rgba Colour) : CimPaint;

/// <summary>A solid stroke.</summary>
/// <param name="Colour">What it paints.</param>
/// <param name="Width">In points, as CIM measures.</param>
/// <param name="Dashes">The dash template, or null for a solid line.</param>
/// <param name="Cap">How the ends are finished, in CIM's vocabulary.</param>
/// <param name="Join">How the corners are finished, in CIM's vocabulary.</param>
public sealed record CimStroke(
    Rgba Colour, double Width, double[]? Dashes, string? Cap, string? Join) : CimPaint;

/// <summary>A point marker.</summary>
/// <param name="Size">Across, in points.</param>
/// <param name="Colour">What it is filled with.</param>
public sealed record CimMarker(double Size, Rgba Colour) : CimPaint;
