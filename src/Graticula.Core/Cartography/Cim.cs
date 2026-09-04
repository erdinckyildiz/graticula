using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
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

    /// <summary>One symbol, sized in proportion to a number.</summary>
    /// <remarks>
    /// <b>Read as a simple renderer carrying a size variable, because that is what it is.</b>
    /// The JavaScript SDK has no proportional renderer at all and expresses the same drawing as
    /// a <c>SimpleRenderer</c> plus a size visual variable, and the specification's own note on
    /// <c>CIMSizeVisualVariable</c> spells out the correspondence: <i>VariableType =
    /// Proportional, unit NOT defined use Expression, MinSize, MinValue, could use MaxSize</i>.
    /// So this needs no new drawing primitive - only the arithmetic that turns one symbol and a
    /// data range into the stops the other three already carry.
    /// </remarks>
    public const string Proportional = "CIMProportionalRenderer";

    /// <summary>A density surface over points.</summary>
    /// <remarks>
    /// <b>The one renderer whose answer does not belong to a feature.</b> Every other kind here
    /// says which symbol a feature gets; a heat map says how crowded a place is, so a pixel's
    /// colour depends on every point near it and there is nothing to resolve per feature. It
    /// needed no new drawing primitive — <c>IMapCanvas.DrawImage</c> was already there — only an
    /// accumulator and a ramp. ADR-052 §3.14.
    /// </remarks>
    public const string HeatMap = "CIMHeatMapRenderer";

    /// <summary>How many colours a heat map's ramp is read into.</summary>
    /// <remarks>
    /// <b>Nine, which is what a continuous ramp costs to carry as stops.</b> Both faces express
    /// the ramp as a list — MapLibre as an interpolate over <c>heatmap-density</c>, Esri as
    /// <c>colorStops</c> — so a continuous CIM ramp has to be sampled somewhere. Nine is the
    /// most any published sequential scheme uses (Brewer stops at nine), and past that the steps
    /// are below what a screen distinguishes on a surface this smooth.
    /// </remarks>
    public const int HeatColours = 9;

    /// <summary>The exponent that makes a symbol's area proportional to its value.</summary>
    /// <remarks>
    /// <b>Published cartography, not read out of anything.</b> A disc whose area is proportional
    /// to a value has a radius proportional to the value's square root. Flannery's correction
    /// replaces the exponent with about 0.57, because readers systematically under-estimate the
    /// area of larger circles - J. J. Flannery, <i>The relative effectiveness of some common
    /// graduated point symbols in the presentation of quantitative data</i>, Cartographica 8(2),
    /// 1971. The specification carries <c>flanneryCompensation</c> as a boolean and does not say
    /// what either curve is; picking these two from the literature is a decision this project
    /// takes and records, and it means a symbol drawn here will not match ArcGIS Pro to the
    /// pixel. ADR-052 §3.10.
    /// </remarks>
    private const double AreaExponent = 0.5;

    /// <summary>Flannery's compensated exponent.</summary>
    private const double FlanneryExponent = 0.5716;

    /// <summary>
    /// How many points the proportional curve is sampled at.
    /// </summary>
    /// <remarks>
    /// <b>Twelve, spaced geometrically, and both halves were measured.</b> The faces carry
    /// straight segments between stops, so a curve becomes an error. Measured against the true
    /// curve over three decades of data with a 4pt minimum symbol: stops spaced evenly by value
    /// are 41% wrong at twelve and never get much better, because a power curve's error is worst
    /// where the values are smallest and even spacing puts almost no stops there. Spaced
    /// geometrically the same twelve stops are wrong by <b>1.22%</b> at worst over three decades
    /// and <b>0.23%</b> over a range of twenty - under a tenth of a point on a 4pt dot, which is
    /// smaller than the difference antialiasing makes.
    /// </remarks>
    private const int ProportionalStops = 12;

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
    /// <returns>The projection, and one sentence per thing it could not carry.</returns>
    public static CimProjection Project(JsonObject body)
    {
        ArgumentNullException.ThrowIfNull(body);

        List<string> notDrawn = [];
        string kind = Text(body["type"]) ?? string.Empty;

        return kind switch
        {
            Simple => ProjectSimple(body, notDrawn) with { Vary = Varying(body, notDrawn) },
            UniqueValue => ProjectUniqueValue(body, notDrawn) with
            {
                Vary = Varying(body, notDrawn),
            },
            ClassBreaks => ProjectClassBreaks(body, notDrawn) with
            {
                Vary = Varying(body, notDrawn),
            },
            Proportional => ProjectProportional(body, notDrawn),
            HeatMap => ProjectHeatMap(body, notDrawn),

            _ => throw new SymbologyException(
                $"'{kind}' is not a renderer this server reads. It reads `{Simple}`, "
                + $"`{UniqueValue}`, `{ClassBreaks}` and `{Proportional}`. A renderer it cannot "
                + "read is refused rather than stored, because a stored document nothing can "
                + "draw is a layer that looks styled and is not. The other five the "
                + "specification defines are `CIMChartRenderer`, `CIMDictionaryRenderer`, "
                + "`CIMDotDensityRenderer` and `CIMRepresentationRenderer`. None reduces to a "
                + "renderer this server already draws, so each is work rather than a reading -- "
                + "but only two are blocked: a dictionary renderer needs a dictionary style this "
                + "server does not hold, and a representation renderer needs a geodatabase's "
                + "representation classes. The other two are arithmetic over primitives that "
                + "already exist."),
        };
    }

    /// <summary>One symbol for every feature.</summary>
    /// <param name="body">The renderer.</param>
    /// <param name="notDrawn">Collects what could not be carried.</param>
    /// <returns>The projection.</returns>
    private static CimProjection ProjectSimple(JsonObject body, List<string> notDrawn)
    {
        CimSymbol symbol = ReadSymbol(body["symbol"], "the renderer's symbol", notDrawn);

        return new CimProjection(
            Simple,
            Field: null,
            [new CimClass(Values: [], UpperBound: null, Text(body["label"]) ?? string.Empty, symbol)],
            Default: null,
            notDrawn);
    }

    /// <summary>One symbol, grown in proportion to a number.</summary>
    /// <remarks>
    /// <para>
    /// <b>It becomes a simple projection carrying a size variable, and that is not a
    /// simplification.</b> <c>Kind</c> is <c>CIMSimpleRenderer</c> here on purpose, and
    /// falsifying it showed which face cares: the Esri face switches on <c>Kind</c> and throws
    /// for one it has no branch for, while the tile face never reads it, because a projection
    /// with no classifying field has already become a constant several lines earlier. So a
    /// fourth <c>Kind</c> would have bought one new branch in <c>CimEsri</c> emitting exactly
    /// what its <c>Simple</c> branch emits. The stored document is untouched and still says
    /// <c>CIMProportionalRenderer</c>; this is the projection, which is by definition the part
    /// that can be drawn.
    /// </para>
    /// <para>
    /// <b>The high end is computed, because the specification does not carry one.</b> A
    /// <c>CIMProportionalRenderer</c> has <c>minSymbol</c>, <c>minDataValue</c> and
    /// <c>maxDataValue</c> and no maximum symbol - the size above the minimum comes from the
    /// proportional rule rather than from a second endpoint, and that rule is the one thing the
    /// document does not state. See <see cref="AreaExponent"/> for where the two curves come
    /// from and what follows from choosing them.
    /// </para>
    /// </remarks>
    /// <param name="body">The renderer.</param>
    /// <param name="notDrawn">Collects what could not be carried.</param>
    /// <returns>The projection.</returns>
    private static CimProjection ProjectProportional(JsonObject body, List<string> notDrawn)
    {
        CimSymbol symbol = ReadSymbol(
            body["minSymbol"], "the renderer's minimum symbol", notDrawn);

        if (body["backgroundSymbol"] is not null)
        {
            // <b>Reported rather than folded in.</b> A background symbol could be prepended to
            // this stack -- `CimSymbol.Paints` is bottom-first and would take it -- but the size
            // variable moves every width in the stack, so a background outline would grow with
            // the data alongside the marker it sits behind. A missing background is visible; a
            // background that swells with the population is not obviously wrong.
            notDrawn.Add(
                "The renderer draws a background symbol underneath its proportional symbols. "
                + "This server draws the proportional symbol alone, because the size the data "
                + "sets would move the background's own widths with it.");
        }

        if (body["useDefaultSymbol"] is JsonValue flag
            && flag.TryGetValue(out bool wanted)
            && wanted)
        {
            notDrawn.Add(
                "The renderer has a default symbol for features it cannot size. This server "
                + "draws one symbol here and has no class for a fallback to sit beside, so "
                + "those features are drawn with the minimum symbol.");
        }

        List<CimVary> vary = Varying(body, notDrawn);

        // <b>Only when the document did not already say it.</b> A proportional renderer inherits
        // `CIMVisualVariableRenderer`, so it may carry a size variable of its own; that one is
        // what the author wrote and this one is arithmetic, so the author's wins.
        if (!vary.Any(v => v.What == CimVaries.Size)
            && Proportion(body, symbol, notDrawn) is { } sizing)
        {
            vary.Add(sizing);
        }

        return new CimProjection(
            Simple,
            Field: null,
            [
                new CimClass(
                    Values: [],
                    UpperBound: null,
                    Text(body["heading"]) ?? string.Empty,
                    symbol),
            ],
            Default: null,
            notDrawn)
        {
            Vary = vary,
        };
    }

    /// <summary>The size variable a proportional renderer stands for.</summary>
    /// <param name="body">The renderer.</param>
    /// <param name="symbol">Its minimum symbol, already read.</param>
    /// <param name="notDrawn">Collects what could not be carried.</param>
    /// <returns>The variable, or null with a sentence saying why not.</returns>
    private static CimVary? Proportion(
        JsonObject body, CimSymbol symbol, List<string> notDrawn)
    {
        if (body["unitSymbolization"] is JsonObject)
        {
            // <b>A different sizing model, not a harder one.</b> With `unitSymbolization` the
            // symbol's size *is* the value, in ground units -- a circle standing for 500 metres
            // is 500 metres across at every scale, so its size in points changes as you zoom.
            // This server sizes markers in points. Drawing it at a fixed size would be right at
            // one scale and wrong at every other, with nothing on the map to say which.
            notDrawn.Add(
                "The renderer sizes its symbol in ground units (`unitSymbolization`), so the "
                + "symbol's size on screen changes with the scale. This server sizes markers in "
                + "points, so the minimum symbol is drawn and does not grow.");

            return null;
        }

        if (Field(body) is not { Length: > 0 } field)
        {
            notDrawn.Add(
                $"A `{Proportional}` names no field this server can read in `field` or "
                + "`valueExpressionInfo`, so there is nothing to size by.");

            return null;
        }

        double? floor = Number(body["minDataValue"]);
        double? ceiling = Number(body["maxDataValue"]);

        if (floor is not { } low || ceiling is not { } high || low <= 0 || high <= low)
        {
            // <b>Zero and below have no proportional size.</b> The rule is a ratio to the
            // smallest value, so a minimum of zero divides by it and a negative one asks for the
            // square root of a negative number. ArcGIS treats those specially and this server
            // says so rather than inventing a treatment.
            notDrawn.Add(
                "The renderer's `minDataValue` and `maxDataValue` are not a range this server "
                + $"can size across (read as {Say(floor)} to {Say(ceiling)}; it needs a positive "
                + "minimum below the maximum, because a proportional size is a ratio to the "
                + "smallest value). The minimum symbol is drawn and does not grow.");

            return null;
        }

        if (SizeOf(symbol) is not { } smallest || smallest <= 0)
        {
            notDrawn.Add(
                "The renderer's minimum symbol has no marker size or stroke width to grow from, "
                + "so nothing can be sized in proportion to it.");

            return null;
        }

        double power = body["flanneryCompensation"] is JsonValue compensate
            && compensate.TryGetValue(out bool on)
            && on
                ? FlanneryExponent
                : AreaExponent;

        List<double> stops = [];
        List<double> sizes = [];
        double step = Math.Pow(high / low, 1.0 / (ProportionalStops - 1));

        for (int i = 0; i < ProportionalStops; i++)
        {
            // <b>The last stop is the maximum itself.</b> Eleven multiplications of an
            // irrational ratio land near it rather than on it, and a legend that reads
            // 9999.9999998 for a maximum of 10000 is a legend somebody has to explain.
            double value = i == ProportionalStops - 1 ? high : low * Math.Pow(step, i);

            stops.Add(value);
            sizes.Add(smallest * Math.Pow(value / low, power));
        }

        return new CimVary(CimVaries.Size, field, stops, [], sizes);
    }

    /// <summary>The size a proportional renderer grows from.</summary>
    /// <remarks>
    /// <b>A marker first, then the widest stroke.</b> Proportional symbols are markers nearly
    /// always; a proportional line width is the other real use, and there the thing that grows
    /// is the widest stroke in the stack rather than a casing under it.
    /// </remarks>
    /// <param name="symbol">The minimum symbol.</param>
    /// <returns>Its size in points, or null when nothing in it has one.</returns>
    private static double? SizeOf(CimSymbol symbol)
    {
        foreach (CimPaint paint in symbol.Paints)
        {
            if (paint is CimMarker marker)
            {
                return marker.Size;
            }
        }

        double widest = 0;

        foreach (CimPaint paint in symbol.Paints)
        {
            if (paint is CimStroke stroke && stroke.Width > widest)
            {
                widest = stroke.Width;
            }
        }

        return widest > 0 ? widest : null;
    }

    /// <summary>A number as a message can print it.</summary>
    /// <param name="value">The number, or null when it was absent or unreadable.</param>
    /// <returns>The number, or the word for its absence.</returns>
    private static string Say(double? value) =>
        value is { } number
            ? number.ToString(CultureInfo.InvariantCulture)
            : "absent";

    /// <summary>A density surface, projected onto what this server can paint.</summary>
    /// <remarks>
    /// <para>
    /// <b>The projection carries no classes and no symbol, and that is honest rather than
    /// lossy.</b> A heat map has neither: it has a field to weigh by, a radius to spread over
    /// and a ramp to colour with. `CimProjection` keeps one empty class so every reader that
    /// walks classes finds nothing rather than throwing, and the surface itself is on `Heat`.
    /// </para>
    /// <para>
    /// <b>`rendererQuality` is not read, and it would not mean the same thing here.</b> In CIM it
    /// trades pixelation for speed on a raster this server does not build the same way; the
    /// surface here is computed at the image's own resolution, which is the quality that CIM's
    /// scale calls best. Costing more than asked is not a loss, so it is not reported as one.
    /// </para>
    /// </remarks>
    /// <param name="body">The renderer.</param>
    /// <param name="notDrawn">Collects what could not be carried.</param>
    /// <returns>The projection.</returns>
    private static CimProjection ProjectHeatMap(JsonObject body, List<string> notDrawn)
    {
        List<Rgba> ramp = Ramp(body["colorScheme"], "the heat map's colour scheme", notDrawn);

        if (ramp.Count < 2)
        {
            // <b>A default rather than a refusal.</b> A heat map with no readable ramp still
            // knows where its points are, and the shape of the surface is most of what it says;
            // refusing would lose that to keep a colour nobody chose either way.
            notDrawn.Add(
                "The heat map's `colorScheme` could not be read as a ramp, so it is drawn in a "
                + "blue-to-red default. The surface is the layer's own; only the colours are "
                + "this server's.");

            ramp = [..(Rgba[])
            [
                new(12, 44, 132, 255), new(34, 94, 168, 255), new(29, 145, 192, 255),
                new(65, 182, 196, 255), new(127, 205, 187, 255), new(199, 233, 180, 255),
                new(255, 237, 160, 255), new(254, 178, 76, 255), new(227, 26, 28, 255),
            ]];
        }

        if (Text(body["field"]) is { Length: > 0 } weighted && Plain(weighted) is null)
        {
            notDrawn.Add(
                $"The heat map weighs features by '{weighted}', which this server cannot read as "
                + "a column name. Every feature is counted as one instead.");
        }

        double radius = Number(body["radius"]) ?? 10;

        if (body["referenceScale"] is not null && Number(body["referenceScale"]) is > 0)
        {
            notDrawn.Add(
                "The heat map has a reference scale, which fixes the search radius in ground "
                + "units so the surface keeps its shape as you zoom. This server spreads the "
                + "radius in points at every scale, so the surface is smoother when zoomed in "
                + "and tighter when zoomed out.");
        }

        return new CimProjection(
            HeatMap,
            Field: null,
            [new CimClass([], null, Text(body["heading"]) ?? string.Empty, new CimSymbol([]))],
            Default: null,
            notDrawn)
        {
            Heat = new CimHeat(
                Plain(Text(body["field"]) ?? string.Empty),
                radius,
                ramp,
                Number(body["maxPixelIntensity"])),
        };
    }

    /// <summary>One symbol per distinct value.</summary>
    /// <param name="body">The renderer.</param>
    /// <param name="notDrawn">Collects what could not be carried.</param>
    /// <returns>The projection.</returns>
    private static CimProjection ProjectUniqueValue(JsonObject body, List<string> notDrawn)
    {
        // <b>`fields` is an array and this server classifies by one.</b> Esri allows three,
        // combined; saying so is better than drawing by the first and letting somebody find out
        // from a map that two thirds of their distinction vanished.
        List<string> fields = (body["fields"] as JsonArray ?? [])
            .Select(f => Text(f))
            .Where(f => !string.IsNullOrEmpty(f))
            .Select(f => f!)
            .ToList();

        // <b>`valueExpressionInfo` when `fields` is empty, which is how Pro writes it now.</b>
        // [D-206](../../../docs/architecture-debt.md): the specification's modern spelling for
        // *which field* is an Arcade expression, and a renderer that uses it carries no `fields`
        // at all. Requiring `fields` refused those documents outright -- louder than the same
        // fault one level down in visual variables ([D-203](../../../docs/architecture-debt.md)),
        // and wrong in the same way.
        //
        // <b>Tried second, not first.</b> `fields` is checked with `Text`, which accepts a
        // column name with a space in it; `Field` applies `Plain`, which does not. Reaching for
        // `Field` first would newly refuse names this server has always read.
        if (fields.Count == 0 && Field(body) is { Length: > 0 } expressed)
        {
            fields.Add(expressed);
        }

        if (fields.Count == 0)
        {
            throw new SymbologyException(
                $"A `{UniqueValue}` names no field in `fields`, and none this server can read in "
                + "`valueExpressionInfo`, so there is nothing to classify by.");
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
                    ReadSymbol(one["symbol"], "a unique-value class", notDrawn)));
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
            Fallback(body, notDrawn),
            notDrawn);
    }

    /// <summary>One symbol per range of a number.</summary>
    /// <param name="body">The renderer.</param>
    /// <param name="notDrawn">Collects what could not be carried.</param>
    /// <returns>The projection.</returns>
    private static CimProjection ProjectClassBreaks(JsonObject body, List<string> notDrawn)
    {
        // <b>`field`, then `valueExpressionInfo` — D-206, and the order is the point.</b> See
        // the unique-value reader above: the plain property is read with `Text` and admits a
        // column name `Plain` would reject, so the Arcade spelling is the fallback rather than
        // the preference.
        string? field = Text(body["field"]);

        if (field is not { Length: > 0 })
        {
            field = Field(body);
        }

        if (field is not { Length: > 0 })
        {
            throw new SymbologyException(
                $"A `{ClassBreaks}` names no `field`, and none this server can read in "
                + "`valueExpressionInfo`, so there is nothing to classify by.");
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
                ReadSymbol(one["symbol"], "a class break", notDrawn)));
        }

        if (classes.Count == 0)
        {
            throw new SymbologyException(
                $"A `{ClassBreaks}` has no `breaks`, so it draws nothing.");
        }

        // <b>Sorted, because `step` needs ascending stops and CIM does not promise them.</b>
        // `showInAscendingOrder` is about the legend, not about the data.
        classes.Sort((a, b) => (a.UpperBound ?? 0).CompareTo(b.UpperBound ?? 0));

        // <b>Three pictures share one renderer type, and the enum is what tells them apart.</b>
        // `GraduatedColor` and `GraduatedSymbol` both come out right without being read, because
        // the difference between them is already in the per-class symbols -- one varies colour,
        // the other size, and this reader draws whatever the symbols say. `UnclassedColor` is
        // the one that does not: it means a continuous ramp across the range rather than a band
        // per class, and drawing it as bands is a visibly different map. The specification says
        // only what the value means, not how the ramp maps onto the breaks, so this reports it
        // rather than inventing an arithmetic that would be wrong in a way nobody could see.
        if (Text(body["classBreakType"]) is { Length: > 0 } shape
            && shape.Contains("Unclassed", StringComparison.OrdinalIgnoreCase))
        {
            notDrawn.Add(
                "The renderer is an unclassed colour ramp (`classBreakType` is "
                + $"'{shape}'), which colours continuously across the range. This server draws "
                + "one band per break, so the map is stepped where it should be smooth.");
        }

        if (body["backgroundSymbol"] is not null)
        {
            notDrawn.Add(
                "The renderer draws a background symbol underneath its graduated symbols. This "
                + "server draws the class symbols alone.");
        }

        CimSymbol? fallback = Fallback(body, notDrawn);
        double? floor = Number(body["minimumBreak"]);

        // <b>A floor with nothing to fall to is a floor that cannot be drawn.</b> Below it the
        // features are outside the classification; ArcGIS draws them with the default symbol or
        // not at all, and this server has no way to say *not at all* in a paint expression that
        // is only about colour. So it says what it does instead of doing something else quietly.
        if (floor is { } bottom && fallback is null)
        {
            notDrawn.Add(
                $"The renderer classifies from {bottom.ToString(CultureInfo.InvariantCulture)} "
                + "upwards and has no default symbol, so this server has nothing to draw the "
                + "features below that with. They are drawn as the first class, which is the "
                + "one place this face is wider than the document.");

            floor = null;
        }

        return new CimProjection(
            ClassBreaks, field, classes, fallback, notDrawn)
        {
            Floor = floor,
        };
    }

    /// <summary>
    /// Reads the renderer's visual variables.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The second of ADR-052's three axes, and it rides on top of the first.</b> A renderer
    /// says which feature gets which symbol; a visual variable says how one property of that
    /// symbol slides continuously with a number. `Counts and Amounts`, `Age` and half of
    /// `Predominance` in ArcGIS are a renderer plus one of these, not renderers of their own.
    /// </para>
    /// <para>
    /// <b>Esri names the field four different ways and this reads all four.</b> Transparency
    /// carries a plain <c>field</c>; colour and size carry <c>expression</c>, usually
    /// <c>$feature.POP</c> — and the specification says of that property that it <i>is used for
    /// Python or VBScript expressions. Arcade expressions will use the ValueExpressionInfo
    /// property</i>, so a variable ArcGIS Pro writes today puts the field in
    /// <c>valueExpressionInfo.expression</c> and leaves <c>expression</c> empty. Refusing the
    /// ones that do not match one spelling would refuse documents Pro itself writes.
    /// </para>
    /// </remarks>
    /// <param name="body">The renderer.</param>
    /// <param name="notDrawn">Collects what could not be carried.</param>
    /// <returns>One entry per variable this server can draw.</returns>
    private static List<CimVary> Varying(JsonObject body, List<string> notDrawn)
    {
        List<CimVary> found = [];

        foreach (JsonObject variable in Objects(body["visualVariables"]))
        {
            string kind = Text(variable["type"]) ?? string.Empty;

            if (Field(variable) is not { Length: > 0 } field)
            {
                notDrawn.Add(
                    $"A `{kind}` names no field this server can read. It reads a plain field "
                    + "name, `$feature.NAME` and `$feature[\"NAME\"]`; an Arcade expression that "
                    + "computes something is not evaluated.");

                continue;
            }

            switch (kind)
            {
                case "CIMColorVisualVariable":
                    if (Ramp(variable["colorRamp"], kind, notDrawn) is { Count: > 1 } colours)
                    {
                        found.Add(new CimVary(
                            CimVaries.Colour, field, Range(variable), colours, []));
                    }

                    break;

                case "CIMSizeVisualVariable":
                    (double[] data, double[] sizes) = Sizes(variable);

                    if (sizes.Length > 1)
                    {
                        found.Add(new CimVary(CimVaries.Size, field, data, [], sizes));
                    }
                    else
                    {
                        notDrawn.Add(
                            "A `CIMSizeVisualVariable` gives no size range this server can read. "
                            + "It reads `minSize`/`maxSize` with `minValue`/`maxValue`, and the "
                            + "`dataValues`/`sizeValues` pair.");
                    }

                    break;

                case "CIMTransparencyVisualVariable":
                    double[] steps = Numbers(variable["dataValues"]) ?? [];
                    double[] alphas = Numbers(variable["transparencyValues"]) ?? [];

                    if (steps.Length > 1 && steps.Length == alphas.Length)
                    {
                        // <b>Transparency, turned into opacity here and only here.</b> CIM's 0
                        // is fully opaque and 100 is invisible; every renderer below this line
                        // thinks in opacity, and converting in one place is what stops half of
                        // them from being inverted.
                        found.Add(new CimVary(
                            CimVaries.Opacity,
                            field,
                            steps,
                            [],
                            [.. alphas.Select(a => Math.Clamp(100 - a, 0, 100) / 100)]));
                    }
                    else
                    {
                        notDrawn.Add(
                            "A `CIMTransparencyVisualVariable` has no matching `dataValues` and "
                            + "`transparencyValues`, so nothing varies.");
                    }

                    break;

                default:
                    notDrawn.Add(
                        $"The renderer has a `{kind}`. This server varies colour, size and "
                        + "transparency by a field; that one is not drawn and is kept in the "
                        + "stored document.");

                    break;
            }
        }

        return found;
    }

    /// <summary>The field a visual variable reads, however it spells it.</summary>
    /// <param name="variable">The variable.</param>
    /// <returns>The column, or null.</returns>
    private static string? Field(JsonObject variable)
    {
        if (Text(variable["field"]) is { Length: > 0 } plain)
        {
            return plain;
        }

        // <b>`valueExpressionInfo` before `expression`, because the specification says so.</b>
        // `expression` is the Python and VBScript slot; Arcade — which is what ArcGIS Pro writes
        // now — goes in `valueExpressionInfo.expression`. Reading only the old slot made every
        // Pro-authored colour or size variable report *names no field this server can read*,
        // and the renderer then drew one flat symbol with no sign that anything had been
        // dropped. Found 2026-09-04 while reading `CIMRenderers.md` for the proportional
        // renderer, and it had been wrong since the reader was written.
        string? text = Text(variable["expression"]);

        if (text is not { Length: > 0 }
            && variable["valueExpressionInfo"] is JsonObject arcade)
        {
            text = Text(arcade["expression"]);
        }

        if (text is not { Length: > 0 } expression)
        {
            return null;
        }

        // `$feature.POP`, `$feature["POP"]`, or a bare name.
        string trimmed = expression.Trim();

        if (trimmed.StartsWith("$feature", StringComparison.OrdinalIgnoreCase))
        {
            string rest = trimmed["$feature".Length..].Trim();

            if (rest.StartsWith('.'))
            {
                // <b>Still checked after the prefix comes off.</b> `$feature.a / $feature.b`
                // is an Arcade expression that begins exactly like a field reference, and
                // taking everything after the dot would ask the source for a column called
                // `a / $feature.b`.
                return Plain(rest[1..]);
            }

            if (rest.StartsWith('[') && rest.EndsWith(']'))
            {
                return Plain(rest[1..^1].Trim().Trim('"', '\''));
            }

            return null;
        }

        return Plain(trimmed);
    }

    /// <summary>A column name, or null when the text computes rather than names.</summary>
    /// <remarks>
    /// <b>Anything with an operator in it is Arcade.</b> This server does not evaluate Arcade,
    /// and reading such a string as a column would fail as a database error a long way from the
    /// document that caused it.
    /// </remarks>
    /// <param name="text">What the document said.</param>
    /// <returns>The name, trimmed, or null.</returns>
    private static string? Plain(string text)
    {
        string name = text.Trim();

        return name.Length > 0 && name.All(c => char.IsLetterOrDigit(c) || c == '_')
            ? name
            : null;
    }

    /// <summary>The two ends of a colour variable's range.</summary>
    /// <param name="variable">The variable.</param>
    /// <returns>Its stops.</returns>
    private static double[] Range(JsonObject variable) =>
        [Number(variable["minValue"]) ?? 0, Number(variable["maxValue"]) ?? 1];

    /// <summary>A size variable's data stops and the sizes they map to.</summary>
    /// <remarks>
    /// <b>Two spellings, because Esri writes both.</b> `minSize`/`maxSize` against
    /// `minValue`/`maxValue` is the two-stop form; `dataValues`/`sizeValues` are parallel arrays
    /// for more than two. Reading only one of them would silently flatten half the documents.
    /// </remarks>
    /// <param name="variable">The variable.</param>
    /// <returns>The stops and the sizes.</returns>
    private static (double[] Data, double[] Sizes) Sizes(JsonObject variable)
    {
        double[] data = Numbers(variable["dataValues"]) ?? [];
        double[] sizes = Numbers(variable["sizeValues"]) ?? [];

        if (data.Length > 1 && data.Length == sizes.Length)
        {
            return (data, sizes);
        }

        if (Number(variable["minSize"]) is { } small && Number(variable["maxSize"]) is { } large)
        {
            return (Range(variable), [small, large]);
        }

        return ([], []);
    }

    /// <summary>A colour ramp, as the list of colours this server interpolates between.</summary>
    /// <param name="node">The ramp.</param>
    /// <param name="where">Which variable, for a message that can be acted on.</param>
    /// <param name="notDrawn">Collects what could not be carried.</param>
    /// <returns>Two or more colours, or fewer when the ramp cannot be read.</returns>
    private static List<Rgba> Ramp(JsonNode? node, string where, List<string> notDrawn)
    {
        if (node is not JsonObject ramp)
        {
            notDrawn.Add($"A `{where}` has no colour ramp, so nothing varies.");

            return [];
        }

        switch (Text(ramp["type"]))
        {
            case "CIMLinearContinuousColorRamp":
            case "CIMPolarContinuousColorRamp":
                return
                [
                    Colour(ramp["fromColor"], where, notDrawn),
                    Colour(ramp["toColor"], where, notDrawn),
                ];

            case "CIMFixedColorRamp":
                List<Rgba> fixedColours =
                    [.. Objects(ramp["colors"]).Select(c => Colour(c, where, notDrawn))];

                if (fixedColours.Count < 2)
                {
                    notDrawn.Add($"A `{where}`'s fixed ramp has fewer than two colours.");
                }

                return fixedColours;

            case "CIMMultipartColorRamp":
                // <b>Flattened end to end.</b> A multipart ramp's weights bend where each part
                // begins; this server spaces the parts evenly and says so, which is a visible
                // difference on a ramp built to emphasise one end.
                List<Rgba> parts = [];

                foreach (JsonObject inner in Objects(ramp["colorRamps"]))
                {
                    parts.AddRange(Ramp(inner, where, notDrawn));
                }

                if (ramp["weights"] is JsonArray { Count: > 0 })
                {
                    notDrawn.Add(
                        $"A `{where}` uses a multipart ramp with weights. This server spaces the "
                        + "parts evenly, so the ramp changes colour at slightly different values "
                        + "than it was authored to.");
                }

                return parts;

            default:
                notDrawn.Add(
                    $"A `{where}` uses a `{Text(ramp["type"]) ?? "(unnamed)"}` colour ramp. This "
                    + "server reads the continuous and fixed ramps; that one is not drawn.");

                return [];
        }
    }

    /// <summary>The symbol for features no class matches, when the renderer offers one.</summary>
    /// <param name="body">The renderer.</param>
    /// <param name="notDrawn">Collects what could not be carried.</param>
    /// <returns>The symbol, or null.</returns>
    private static CimSymbol? Fallback(JsonObject body, List<string> notDrawn)
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
            : ReadSymbol(body["defaultSymbol"], "the default symbol", notDrawn);
    }

    /// <summary>
    /// Reads a <c>CIMSymbolReference</c> down to the layers this server paints with.
    /// </summary>
    /// <param name="node">The reference, or a bare symbol.</param>
    /// <param name="where">Where this symbol sits, for a message that can be acted on.</param>
    /// <param name="notDrawn">Collects what could not be carried.</param>
    /// <returns>The symbol.</returns>
    private static CimSymbol ReadSymbol(
        JsonNode? node, string where, List<string> notDrawn)
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

        return new CimSymbol(paints);
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
                Num(colour.R), Num(colour.G), Num(colour.B), Num(Percent(colour.A))),
        };

    /// <summary>
    /// A number that reads back as whatever type asks for it.
    /// </summary>
    /// <remarks>
    /// <b>Not <c>JsonValue.Create</c>, and the difference is a whole class of defect.</b> A
    /// <c>JsonValue</c> created from an <c>int</c> is a <c>JsonValue&lt;int&gt;</c> and refuses
    /// <c>GetValue&lt;double&gt;</c>; one created from a <c>double</c> refuses
    /// <c>GetValue&lt;int&gt;</c>. Both serialise identically, so the document on the wire looks
    /// right and every consumer that reads it in memory throws. Going through a
    /// <c>JsonElement</c> gives a value backed by the parser, which converts the way a document
    /// read from text does. Measured twice on 2026-09-03, in the round trip and in the Esri
    /// face.
    /// </remarks>
    /// <param name="value">The number.</param>
    /// <returns>The node.</returns>
    private static JsonValue Num(double value) =>
        JsonValue.Create(JsonSerializer.SerializeToElement(value))!;

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
    IReadOnlyList<string> NotDrawn)
{
    /// <summary>What slides continuously with a number, on top of the classes.</summary>
    public IReadOnlyList<CimVary> Vary { get; init; } = [];

    /// <summary>The density surface, for a heat map. Null for every other renderer.</summary>
    public CimHeat? Heat { get; init; }

    /// <summary>The bottom of the first class, for a class-breaks renderer.</summary>
    /// <remarks>
    /// <b>The classification's floor, and it is not decoration — [D-205](../../../docs/architecture-debt.md).</b>
    /// A <c>CIMClassBreak</c> carries only its <c>upperBound</c>; the bottom of the whole
    /// classification is <c>minimumBreak</c> on the renderer, and a value below it is
    /// <i>outside the classification</i> rather than inside the first class. Without it the
    /// derived <c>step</c> starts at negative infinity, so a population choropleth floored at
    /// 1,000 draws every village in the colour of a small town — a picture with nothing visibly
    /// wrong with it.
    /// </remarks>
    public double? Floor { get; init; }
}

/// <summary>Which property of a symbol a visual variable moves.</summary>
public enum CimVaries
{
    /// <summary>The colour it is painted in.</summary>
    Colour = 1,

    /// <summary>How wide a stroke is, or how large a marker.</summary>
    Size = 2,

    /// <summary>How opaque it is, from 0 to 1.</summary>
    Opacity = 3,
}

/// <summary>One property of a symbol, sliding with the value of one field.</summary>
/// <param name="What">Which property moves.</param>
/// <param name="Field">The column it reads.</param>
/// <param name="Stops">The values, ascending.</param>
/// <param name="Colours">One per stop, for a colour variable.</param>
/// <param name="Numbers">One per stop, for a size or opacity variable.</param>
public sealed record CimVary(
    CimVaries What,
    string Field,
    IReadOnlyList<double> Stops,
    IReadOnlyList<Rgba> Colours,
    IReadOnlyList<double> Numbers);

/// <summary>A density surface: what to weigh by, how far it spreads, and its colours.</summary>
/// <param name="Field">The column to weigh by, or null to count every feature as one.</param>
/// <param name="Radius">How far one feature's heat spreads, in points.</param>
/// <param name="Ramp">Two or more colours, coolest first.</param>
/// <param name="Ceiling">
/// The density that reaches the ramp's last colour, or null to scale each image against its own
/// peak. <b>A fixed ceiling is what makes two tiles of one layer comparable</b>; without one,
/// every image is its own scale and a quiet corner looks as hot as a busy one.
/// </param>
public sealed record CimHeat(
    string? Field, double Radius, IReadOnlyList<Rgba> Ramp, double? Ceiling);

/// <summary>One symbol and what it stands for.</summary>
/// <param name="Values">The field values this class matches, for a unique-value renderer.</param>
/// <param name="UpperBound">The top of this class's range, for class breaks.</param>
/// <param name="Label">What a legend calls it.</param>
/// <param name="Symbol">What it is drawn with.</param>
public sealed record CimClass(
    IReadOnlyList<string> Values, double? UpperBound, string Label, CimSymbol Symbol);

/// <summary>A symbol, as a stack of the layers this server paints with.</summary>
/// <remarks>
/// <b>It does not carry the layer's geometry, and that is deliberate.</b> Which symbol layers
/// a stack holds is what decides how it is painted; the layer's declared geometry decides
/// nothing here, and a parameter that decides nothing is one somebody eventually passes wrongly
/// with no test able to notice.
/// </remarks>
/// <param name="Paints">Bottom first, so a casing precedes the line that sits on it.</param>
public sealed record CimSymbol(IReadOnlyList<CimPaint> Paints);

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
