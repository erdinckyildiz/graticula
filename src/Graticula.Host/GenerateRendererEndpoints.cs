using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Cartography;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Platform.Catalog;
using Graticula.Platform.Postgres;
using Graticula.Providers.PostGis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Graticula.Host;

/// <summary>
/// Builds a renderer from a field, a method and the data.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-052](../../docs/adr/ADR-052-the-canonical-symbology-document-is-cim.md) §3.13.</b> A
/// unique-value renderer is a list of a field's distinct values and a class-breaks renderer is a
/// set of bounds computed from a field's distribution. Until this existed, the console asked
/// somebody to type both — so the graphical editor was usable for exactly the one renderer that
/// needs no data.
/// </para>
/// <para>
/// <b>Behind ArcGIS's own operation rather than inside the console.</b>
/// <c>generateRenderer</c> takes a <c>classificationDef</c> and returns a renderer, and it is
/// what every ArcGIS client already calls to do this. Putting the arithmetic here serves the
/// console and those clients from one implementation; putting it in JavaScript would have served
/// the console alone and left the operation returning 404 to everybody else.
/// </para>
/// <para>
/// <b>One round trip for the numbers.</b> A classification needs the minimum, the maximum,
/// sometimes the mean and standard deviation, and for two methods a list of quantiles —
/// <see cref="Classification.Fractions"/> says which. All of them are asked for in a single
/// <c>outStatistics</c> query, because they are all aggregates over the same rows and asking
/// separately would read the column five times.
/// </para>
/// </remarks>
public static class GenerateRendererEndpoints
{
    /// <summary>The most distinct values a unique-value renderer is built from.</summary>
    /// <remarks>
    /// <para>
    /// <b>256, and it was 64 for a day, which was a judgement rather than a measurement.</b> The
    /// first version reasoned that a field with hundreds of values is *usually an identifier*
    /// and refused there. That is true of some fields and quite wrong about others: Turkey has
    /// **81 provinces**, and one colour per province is an ordinary map that the old ceiling
    /// refused outright. Found 2026-09-04 by the owner asking why they could not colour a layer
    /// by its name.
    /// </para>
    /// <para>
    /// <b>The number is now taken from the thing that actually stops it.</b> A stored symbology
    /// document is capped at <see cref="SymbologyConversion.MaximumCharacters"/> — 262,144, with
    /// a database check constraint behind it. Measured: one unique-value class costs <b>478</b>
    /// characters for a polygon symbol and <b>690</b> for a point, so the document runs out at
    /// about 548 and 379 classes. 256 fits every geometry with room to spare and is the largest
    /// round number that does.
    /// </para>
    /// <para>
    /// <b>What is not the reason is readability, and saying so matters.</b> Nobody can tell 256
    /// colours apart and no legend of that size is read — but that is the author's business, not
    /// the server's. This refuses when it cannot store the answer, and says the rest.
    /// </para>
    /// </remarks>
    public const int MostValues = 256;

    /// <summary>How far the distinct read goes, so a refusal can name a number.</summary>
    /// <remarks>
    /// <b>Twenty times the ceiling, and the cost is the same shape.</b> `DISTINCT` is computed
    /// over the whole column before any limit applies, so reading 5,120 values rather than 257
    /// buys transfer rather than work — and it turns *more than 256* into *1,394*, which is the
    /// number that tells an operator whether to look for another field or another question.
    /// Past this it says *more than 5,119*, because at that point the exact figure has stopped
    /// changing what anybody would do.
    /// </remarks>
    public const int Counted = (MostValues * 20) + 1;

    /// <summary>Registers the operation under one URL prefix.</summary>
    /// <param name="app">The application.</param>
    /// <param name="prefix">The services prefix this face is mapped under.</param>
    public static void Map(IEndpointRouteBuilder app, string prefix)
    {
        ArgumentNullException.ThrowIfNull(app);

        // <b>Both verbs, because both are used.</b> ArcGIS documents a POST and every browser
        // and script reaches for the GET; the operation reads and changes nothing, so refusing
        // the GET would be ceremony.
        app.MapGet(
            $"{prefix}/{{serviceName}}/FeatureServer/{{layerId:int}}/generateRenderer",
            GenerateAsync)
            .Governed(SharingGovernedExtensions.ByService);

        app.MapPost(
            $"{prefix}/{{serviceName}}/FeatureServer/{{layerId:int}}/generateRenderer",
            GenerateAsync)
            .Governed(SharingGovernedExtensions.ByService);
    }

    private static async Task GenerateAsync(
        HttpContext context,
        string serviceName,
        int layerId,
        CatalogFallback catalog,
        ServiceContexts contexts,
        CancellationToken cancellation)
    {
        PublishedLayer? layer = await ServiceLookup
            .LayerAsync(context, catalog, serviceName, layerId, cancellation)
            .ConfigureAwait(false);

        if (layer is null)
        {
            return;
        }

        string? definition = await ReadDefinitionAsync(context).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(definition))
        {
            await RefuseAsync(
                context,
                "`classificationDef` is required. It is a JSON object with a `type` of "
                + "`classBreaksDef` or `uniqueValueDef`.")
                .ConfigureAwait(false);

            return;
        }

        (IFeatureSource source, LayerDescription described) = await contexts
            .GetAsync(layer, cancellation).ConfigureAwait(false);

        try
        {
            JsonObject asked = JsonNode.Parse(definition) as JsonObject
                ?? throw new SymbologyException("`classificationDef` is not a JSON object.");

            JsonObject renderer = await BuildAsync(
                asked, source, described, layer.GeometryType, cancellation)
                .ConfigureAwait(false);

            await Results.Content(renderer.ToJsonString(), "application/json")
                .ExecuteAsync(context).ConfigureAwait(false);
        }
        catch (SymbologyException refused)
        {
            await RefuseAsync(context, refused.Message).ConfigureAwait(false);
        }
        catch (JsonException broken)
        {
            await RefuseAsync(context, $"`classificationDef` is not valid JSON: {broken.Message}")
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Builds a renderer from a classification definition and the layer's own data.
    /// </summary>
    /// <remarks>
    /// <b>Shared, because the console asks the same question through a different door.</b> The
    /// REST operation is what an ArcGIS client calls; the console reaches
    /// <c>/admin/layers/{name}/classify</c>, which resolves a layer by name and wants the answer
    /// as CIM rather than as a <c>drawingInfo</c>. Two doors, one implementation — a second copy
    /// of this arithmetic is how the two faces start disagreeing about what a quantile is.
    /// </remarks>
    /// <param name="asked">The classification definition.</param>
    /// <param name="source">The layer's source, still wrapped in whatever wraps it.</param>
    /// <param name="described">Its fields, for checking the one being classified.</param>
    /// <param name="geometry">What it is made of, for the default symbol.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>An Esri renderer.</returns>
    /// <exception cref="SymbologyException">The request or the data cannot carry it.</exception>
    internal static async Task<JsonObject> BuildAsync(
        JsonObject asked,
        IFeatureSource source,
        LayerDescription described,
        GeometryKind geometry,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(asked);

        // <b>The connection lease first, then the provider inside it.</b> A source arrives
        // wrapped in `BudgetedFeatureSource` -- ADR-007 §4.8's connection cap -- and the
        // statistics below are the provider's own methods rather than `IFeatureSource`'s, so it
        // has to be unwrapped. Taking the lease before unwrapping is what keeps this inside the
        // bound: a classification is three aggregates and a sort over a whole column, which is
        // not a cheap statement to issue outside the cap.
        BudgetedFeatureSource? budgeted = source as BudgetedFeatureSource;

        using ConnectionBudget.Lease lease = budgeted is not null
            ? await budgeted.LeaseAsync(cancellation).ConfigureAwait(false)
            : default;

        IFeatureSource inner = budgeted?.Inner ?? source;

        if (inner is not PostGisFeatureSource postgis)
        {
            throw new SymbologyException(
                "This layer is not served from PostGIS, and generating a renderer needs the "
                + "statistics only that provider computes.");
        }

        return Text(asked["type"]) switch
        {
            "classBreaksDef" => await BreaksAsync(
                asked, postgis, described, geometry, cancellation).ConfigureAwait(false),

            "uniqueValueDef" => await ValuesAsync(
                asked, postgis, described, geometry, cancellation).ConfigureAwait(false),

            string other => throw new SymbologyException(
                $"`classificationDef.type` of '{other}' is neither `classBreaksDef` nor "
                + "`uniqueValueDef`."),

            _ => throw new SymbologyException(
                "`classificationDef` needs a `type` of `classBreaksDef` or `uniqueValueDef`."),
        };
    }

    /// <summary>Reads the definition from wherever the caller put it.</summary>
    /// <remarks>
    /// <b>Three places, because three kinds of client use three.</b> A script puts it in the
    /// query string, ArcGIS's own documentation shows a form post, and anything modern sends a
    /// JSON body. Reading all three costs a few lines and saves everybody a support question.
    /// </remarks>
    private static async Task<string?> ReadDefinitionAsync(HttpContext context)
    {
        if (context.Request.Query["classificationDef"].FirstOrDefault() is { Length: > 0 } fromUrl)
        {
            return fromUrl;
        }

        if (!HttpMethods.IsPost(context.Request.Method))
        {
            return null;
        }

        if (context.Request.HasFormContentType)
        {
            IFormCollection form = await context.Request.ReadFormAsync().ConfigureAwait(false);

            return form["classificationDef"].FirstOrDefault();
        }

        using StreamReader reader = new(context.Request.Body);

        string body = await reader.ReadToEndAsync().ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        // A JSON body may be the definition itself or an envelope carrying it.
        if (JsonNode.Parse(body) is JsonObject envelope
            && envelope["classificationDef"] is JsonNode inner)
        {
            return inner.ToJsonString();
        }

        return body;
    }

    /// <summary>A class-breaks renderer over a numeric field.</summary>
    private static async Task<JsonObject> BreaksAsync(
        JsonObject asked,
        PostGisFeatureSource source,
        LayerDescription described,
        GeometryKind geometry,
        CancellationToken cancellation)
    {
        string field = Column(asked["classificationField"], described, "classificationField");
        ClassifyBy method = Method(Text(asked["classificationMethod"]));
        int classes = (int)(Number(asked["breakCount"]) ?? 5);
        double interval = Number(asked["classificationIntervalSize"]) ?? 1;

        if (Text(asked["normalizationType"]) is { Length: > 0 } normalisation)
        {
            throw new SymbologyException(
                $"`normalizationType` of '{normalisation}' is not applied. This server "
                + "classifies the field as it stands; normalising it would classify a number "
                + "that is in no column, and the legend would name a field whose values it does "
                + "not hold.");
        }

        IReadOnlyList<double> fractions = Classification.Fractions(method, classes);

        List<StatisticRequest> wanted =
        [
            new(StatisticKind.Min, field, "lo"),
            new(StatisticKind.Max, field, "hi"),
            new(StatisticKind.Avg, field, "mean"),
            new(StatisticKind.StdDev, field, "sd"),
        ];

        for (int i = 0; i < fractions.Count; i++)
        {
            wanted.Add(new StatisticRequest(
                StatisticKind.PercentileContinuous, field,
                string.Create(CultureInfo.InvariantCulture, $"q{i}"), fractions[i]));
        }

        IReadOnlyDictionary<string, object?> row = await OneRowAsync(
            source, wanted, cancellation).ConfigureAwait(false);

        Distribution distribution = new(
            Read(row, "lo") ?? double.NaN,
            Read(row, "hi") ?? double.NaN,
            Read(row, "mean") ?? 0,
            Read(row, "sd") ?? 0,
            [.. Enumerable.Range(0, fractions.Count)
                .Select(i => Read(row, string.Create(CultureInfo.InvariantCulture, $"q{i}")) ?? 0)]);

        IReadOnlyList<double> bounds = Classification.Bounds(
            method, classes, distribution, interval);

        JsonObject baseSymbol = Symbol(asked["baseSymbol"], geometry);
        List<Rgba> ramp = Ramp(asked["colorRamp"], bounds.Count);

        JsonArray infos = [];
        double previous = distribution.Minimum;

        for (int i = 0; i < bounds.Count; i++)
        {
            infos.Add(new JsonObject
            {
                ["classMinValue"] = previous,
                ["classMaxValue"] = bounds[i],
                ["label"] = Label(previous, bounds[i]),
                ["description"] = string.Empty,
                ["symbol"] = Painted(baseSymbol, ramp[i]),
            });

            previous = bounds[i];
        }

        return new JsonObject
        {
            ["type"] = "classBreaks",
            ["field"] = field,
            ["minValue"] = distribution.Minimum,
            ["classificationMethod"] = Named(method),
            ["classBreakInfos"] = infos,
        };
    }

    /// <summary>A unique-value renderer over one field's distinct values.</summary>
    private static async Task<JsonObject> ValuesAsync(
        JsonObject asked,
        PostGisFeatureSource source,
        LayerDescription described,
        GeometryKind geometry,
        CancellationToken cancellation)
    {
        JsonArray named = asked["uniqueValueFields"] as JsonArray
            ?? throw new SymbologyException(
                "`uniqueValueDef` needs `uniqueValueFields`, an array of field names.");

        if (named.Count == 0)
        {
            throw new SymbologyException(
                "`uniqueValueFields` is empty, so there is nothing to classify by.");
        }

        if (named.Count > 3)
        {
            throw new SymbologyException(
                $"`uniqueValueFields` names {named.Count} fields and ArcGIS classifies by at "
                + "most three, so a renderer built from more could not be read back by any "
                + "client — including this server's own reader.");
        }

        List<string> fields = [.. named.Select(n => Column(n, described, "uniqueValueFields"))];
        string delimiter = Text(asked["fieldDelimiter"]) is { Length: > 0 } between
            ? between
            : ", ";

        // <b>Grouped and counted, ordered by the count.</b> Which values are the most common is
        // the question a classification has to answer before it can decide what to draw and what
        // to leave to the default symbol — ArcGIS's own answer, in Map Viewer, is *only the ten
        // with the highest counts are shown; the remaining are automatically grouped into the
        // Other category*. Reading them in alphabetical order and taking the first N would put
        // the classes somebody cares about into Other because their names begin with a late
        // letter.
        List<StatisticRequest> counting =
        [
            new(StatisticKind.Count, fields[0], "n"),
        ];

        FeatureQuery query = new(
            limit: Counted,
            includeGeometry: false,
            statistics: counting,
            groupBy: fields,
            orderBy: [new Graticula.Features.SortKey("n", Descending: true)]);

        List<(string Value, long Count)> counted = [];

        foreach (IReadOnlyDictionary<string, object?> row in
            await source.StatisticsAsync(query, cancellation).ConfigureAwait(false))
        {
            List<string> parts = [];

            foreach (string one in fields)
            {
                parts.Add(row.TryGetValue(one, out object? held) && held is not null
                    ? Convert.ToString(held, CultureInfo.InvariantCulture) ?? string.Empty
                    : string.Empty);
            }

            counted.Add((
                string.Join(delimiter, parts),
                row.TryGetValue("n", out object? many) && many is not null
                    ? Convert.ToInt64(many, CultureInfo.InvariantCulture)
                    : 0));
        }

        if (counted.Count == 0)
        {
            throw new SymbologyException(
                $"'{string.Join(delimiter, fields)}' has no values in this layer, so there is "
                + "nothing to classify.");
        }

        // <b>What falls to Other, said as a number rather than as "the rest".</b>
        int shown = Math.Min(counted.Count, MostValues);
        int hidden = counted.Count - shown;
        long behind = 0;

        for (int i = shown; i < counted.Count; i++)
        {
            behind += counted[i].Count;
        }

        JsonObject baseSymbol = Symbol(asked["baseSymbol"], geometry);
        JsonObject? ramp = asked["colorRamp"] as JsonObject;

        // <b>How many classes fit is measured, not calculated, and this is the second time that
        // lesson has been learned here.</b> `MostValues` was set from a cost per class of 478
        // characters for a polygon and 690 for a point, and both were measured on the wrong
        // document: <b>what is stored is the CIM, and CIM is far heavier than Esri's
        // renderer</b>. The same 256 classes of the owner's place names are <b>72,986</b>
        // characters as a `drawingInfo` and <b>165,470</b> as CIM — 646 a class rather than 274 —
        // because a point symbol becomes a `CIMVectorMarker` wrapping a graphic wrapping a
        // polygon symbol rather than four numbers and a style name.
        //
        // <b>So it builds, converts, weighs, and if it is over the cap builds again with fewer.</b>
        // The cost per class depends on the geometry, on the symbol somebody supplied and on how
        // long the values are, and no constant is right for all three. Two passes settle it in
        // every case seen; the loop is bounded at four so a pathological symbol cannot spin.
        int allowed = Math.Min(counted.Count, MostValues);
        JsonObject built = Renderer(allowed);

        for (int pass = 0; pass < 4 && allowed > 1; pass++)
        {
            int size = Stored(built, geometry);

            if (size <= SymbologyConversion.MaximumCharacters)
            {
                break;
            }

            // A tenth off the proportional answer, because the overhead outside the classes is
            // not proportional and a second pass that lands just over would cost a third.
            int fewer = (int)(allowed
                * ((double)SymbologyConversion.MaximumCharacters / size) * 0.9);

            allowed = Math.Clamp(fewer, 1, allowed - 1);
            built = Renderer(allowed);
        }

        return built;

        // <b>The renderer for the first `many` values, and the rest into `Other`.</b>
        JsonObject Renderer(int many)
        {
            List<string> values = [.. counted.Take(many).Select(c => c.Value)];
            List<Rgba> colours = ramp is not null
                ? Ramp(ramp, values.Count)
                : Distinct(values.Count);

            JsonArray infos = [];

            for (int i = 0; i < values.Count; i++)
            {
                infos.Add(new JsonObject
                {
                    ["value"] = values[i],
                    ["label"] = values[i],
                    ["description"] = string.Empty,
                    ["symbol"] = Painted(baseSymbol, colours[i]),
                });
            }

            JsonObject renderer = new()
            {
                ["type"] = "uniqueValue",
                ["field1"] = fields[0],
                ["field2"] = fields.Count > 1 ? fields[1] : null,
                ["field3"] = fields.Count > 2 ? fields[2] : null,
                ["fieldDelimiter"] = delimiter,
                ["uniqueValueInfos"] = infos,
            };

            // <b>An "Other" class rather than a refusal, which is what ArcGIS does.</b> Map
            // Viewer shows only the ten most common categories and groups the rest into Other;
            // this server refused the whole classification until 2026-09-04, which turned a
            // field with too many values into no map at all. The label carries the numbers,
            // because *Other* on its own does not say whether it is three features or three
            // hundred thousand.
            int rest = counted.Count - values.Count;

            if (rest > 0)
            {
                long left = 0;

                for (int i = values.Count; i < counted.Count; i++)
                {
                    left += counted[i].Count;
                }

                renderer["defaultSymbol"] = Painted(baseSymbol, new Rgba(170, 170, 170, 255));
                renderer["defaultLabel"] = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Other ({rest:N0} more value{(rest == 1 ? string.Empty : "s")}, "
                    + $"{left:N0} feature{(left == 1 ? string.Empty : "s")})");
            }

            return renderer;
        }
    }

    /// <summary>How long this renderer would be once stored, in characters.</summary>
    /// <remarks>
    /// <b>As CIM, because that is what is stored.</b> A renderer measured in Esri's own
    /// vocabulary is measured in the wrong one: 256 classes of the owner's place names are 274
    /// characters each there and 646 as CIM, because a point symbol becomes a `CIMVectorMarker`
    /// wrapping a graphic wrapping a polygon symbol. Measuring the wrong document is how a
    /// ceiling comes to promise something the store refuses.
    /// </remarks>
    /// <param name="renderer">The Esri renderer.</param>
    /// <param name="geometry">What the layer is made of.</param>
    /// <returns>The length of the canonical document.</returns>
    private static int Stored(JsonObject renderer, GeometryKind geometry)
    {
        try
        {
            return CimEsri.FromDrawingInfo(
                new JsonObject { ["renderer"] = renderer.DeepClone() }, geometry)
                .Renderer.ToJsonString().Length;
        }
        catch (SymbologyException)
        {
            // <b>A renderer that will not convert is not this method's problem to report.</b>
            // The caller converts it too, a few lines later, and its refusal carries the reason.
            return 0;
        }
    }

    /// <summary>Runs one statistics query and returns its single row.</summary>
    private static async Task<IReadOnlyDictionary<string, object?>> OneRowAsync(
        PostGisFeatureSource source,
        IReadOnlyList<StatisticRequest> wanted,
        CancellationToken cancellation)
    {
        FeatureQuery query = new(
            limit: 1,
            includeGeometry: false,
            statistics: wanted);

        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows =
            await source.StatisticsAsync(query, cancellation).ConfigureAwait(false);

        return rows.Count > 0
            ? rows[0]
            : throw new SymbologyException(
                "The statistics query returned no rows, so this layer has nothing to classify.");
    }

    private static double? Read(IReadOnlyDictionary<string, object?> row, string name) =>
        row.TryGetValue(name, out object? value) && value is not null
            ? Convert.ToDouble(value, CultureInfo.InvariantCulture)
            : null;

    /// <summary>Esri's classification names, mapped onto CIM's.</summary>
    /// <remarks>
    /// <b>Two vocabularies for one list, and this is the only place they meet.</b> The REST face
    /// spells them <c>esriClassifyNaturalBreaks</c>; CIM spells the same seven
    /// <c>NaturalBreaks</c>, and the stored document uses CIM's.
    /// </remarks>
    private static ClassifyBy Method(string? esri) => (esri ?? string.Empty) switch
    {
        "" or "esriClassifyNaturalBreaks" => ClassifyBy.NaturalBreaks,
        "esriClassifyEqualInterval" => ClassifyBy.EqualInterval,
        "esriClassifyQuantile" => ClassifyBy.Quantile,
        "esriClassifyStandardDeviation" => ClassifyBy.StandardDeviation,
        "esriClassifyGeometricalInterval" => ClassifyBy.GeometricalInterval,
        "esriClassifyDefinedInterval" => ClassifyBy.DefinedInterval,
        "esriClassifyManual" => ClassifyBy.Manual,

        _ => throw new SymbologyException(
            $"`classificationMethod` of '{esri}' is not one this server knows. It knows "
            + "esriClassifyEqualInterval, esriClassifyDefinedInterval, "
            + "esriClassifyGeometricalInterval, esriClassifyStandardDeviation, "
            + "esriClassifyQuantile and esriClassifyNaturalBreaks."),
    };

    /// <summary>The CIM spelling, for the document the answer will be stored as.</summary>
    private static string Named(ClassifyBy method) => method.ToString();

    /// <summary>A field name, checked against the layer before it reaches SQL.</summary>
    private static string Column(JsonNode? node, LayerDescription described, string where)
    {
        string asked = Text(node) ?? string.Empty;

        foreach (FieldDescription field in described.Fields)
        {
            if (field.Name.Equals(asked, StringComparison.OrdinalIgnoreCase))
            {
                return field.Name;
            }
        }

        throw new SymbologyException(
            $"`{where}` names '{asked}', which is not a field of this layer.");
    }

    /// <summary>The symbol every class is a recoloured copy of.</summary>
    private static JsonObject Symbol(JsonNode? given, GeometryKind geometry)
    {
        if (given is JsonObject supplied)
        {
            return (JsonObject)supplied.DeepClone();
        }

        // <b>A default per geometry, because a renderer with no symbol draws nothing.</b> Esri
        // makes `baseSymbol` optional and every client that omits it expects something sensible
        // rather than an error.
        return geometry switch
        {
            GeometryKind.Point or GeometryKind.MultiPoint => new JsonObject
            {
                ["type"] = "esriSMS",
                ["style"] = "esriSMSCircle",
                ["color"] = Colour(new Rgba(0, 122, 194, 255)),
                ["size"] = 8,
                ["outline"] = new JsonObject
                {
                    ["type"] = "esriSLS",
                    ["style"] = "esriSLSSolid",
                    ["color"] = Colour(new Rgba(255, 255, 255, 255)),
                    ["width"] = 0.75,
                },
            },

            GeometryKind.LineString or GeometryKind.MultiLineString => new JsonObject
            {
                ["type"] = "esriSLS",
                ["style"] = "esriSLSSolid",
                ["color"] = Colour(new Rgba(0, 122, 194, 255)),
                ["width"] = 1.5,
            },

            _ => new JsonObject
            {
                ["type"] = "esriSFS",
                ["style"] = "esriSFSSolid",
                ["color"] = Colour(new Rgba(0, 122, 194, 255)),
                ["outline"] = new JsonObject
                {
                    ["type"] = "esriSLS",
                    ["style"] = "esriSLSSolid",
                    ["color"] = Colour(new Rgba(110, 110, 110, 255)),
                    ["width"] = 0.4,
                },
            },
        };
    }

    /// <summary>A copy of the base symbol in one class's colour.</summary>
    /// <remarks>
    /// <b>`color` is the same property on all three simple symbol types</b> — a fill's fill, a
    /// line's line, a marker's fill — so one substitution covers every geometry. The outline is
    /// left alone deliberately: recolouring it too makes neighbouring classes bleed into each
    /// other, which is the whole reason a choropleth has outlines.
    /// </remarks>
    private static JsonObject Painted(JsonObject baseSymbol, Rgba colour)
    {
        JsonObject copy = (JsonObject)baseSymbol.DeepClone();

        copy["color"] = Colour(colour);

        return copy;
    }

    private static JsonArray Colour(Rgba colour) => [colour.R, colour.G, colour.B, colour.A];

    /// <summary>One colour per class, from the ramp the caller gave or a default.</summary>
    /// <remarks>
    /// <b>The default is a single-hue sequential ramp, and that is a cartographic choice.</b> A
    /// classification is an ordered thing, so its colours have to be orderable by eye; a
    /// rainbow is not, which is why every guide since Brewer has said so. Pale to deep in one
    /// hue reads as *less* to *more* without a legend.
    /// </remarks>
    private static List<Rgba> Ramp(JsonNode? given, int classes)
    {
        Rgba from = new(222, 235, 247, 255);
        Rgba to = new(8, 48, 107, 255);

        if (given is JsonObject ramp)
        {
            if (Read(ramp["fromColor"]) is { } start)
            {
                from = start;
            }

            if (Read(ramp["toColor"]) is { } end)
            {
                to = end;
            }
        }

        if (classes <= 1)
        {
            return [to];
        }

        List<Rgba> colours = [];

        for (int i = 0; i < classes; i++)
        {
            double at = (double)i / (classes - 1);

            colours.Add(new Rgba(
                Between(from.R, to.R, at),
                Between(from.G, to.G, at),
                Between(from.B, to.B, at),
                Between(from.A, to.A, at)));
        }

        return colours;

        static byte Between(byte a, byte b, double at) =>
            (byte)Math.Clamp(Math.Round(a + ((b - a) * at)), 0, 255);

        static Rgba? Read(JsonNode? node)
        {
            if (node is not JsonArray parts || parts.Count < 3)
            {
                return null;
            }

            byte At(int i) =>
                i < parts.Count && parts[i] is JsonValue value && value.TryGetValue(out double v)
                    ? (byte)Math.Clamp(v, 0, 255)
                    : (byte)255;

            return new Rgba(At(0), At(1), At(2), At(3));
        }
    }

    /// <summary>Colours for classes that have no order.</summary>
    /// <remarks>
    /// <para>
    /// <b>The seven this project already ships come first</b> —
    /// <c>GeneratedSymbology.Palette</c>, chosen to stay apart from each other and to survive
    /// the common colour-blindnesses. A map of five or six categories should get the palette
    /// somebody thought about.
    /// </para>
    /// <para>
    /// <b>Past the seventh, the hue turns by the golden angle.</b> The first version repeated
    /// the seven at three quarters of the lightness, which for eighty classes gives twelve
    /// rounds of dimming and pairs nobody can tell apart. Stepping the hue by 137.508° — the
    /// golden angle — spreads any number of hues as evenly as they can be spread, because
    /// consecutive multiples of it never land near each other; it is the same argument that puts
    /// a sunflower's seeds where they are. Lightness and saturation alternate slightly as well,
    /// so that neighbouring classes differ in more than hue for a reader who cannot see one.
    /// </para>
    /// </remarks>
    /// <param name="classes">How many are needed.</param>
    /// <returns>One colour each.</returns>
    internal static List<Rgba> Distinct(int classes)
    {
        List<Rgba> colours = [];

        for (int i = 0; i < classes; i++)
        {
            if (i < GeneratedSymbology.Palette.Length)
            {
                (byte red, byte green, byte blue) =
                    GeneratedSymbology.Bytes(GeneratedSymbology.Palette[i]);

                colours.Add(new Rgba(red, green, blue, 255));

                continue;
            }

            // Past the palette, `Spread` takes over: it chooses against what is already
            // taken rather than following a rule that cannot see it.
            break;
        }

        return colours.Count >= classes ? colours : Spread(colours, classes);
    }

    /// <summary>
    /// Fills a palette out to <paramref name="classes"/> by taking, each time, the colour
    /// furthest from everything already taken.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Furthest-first, because no fixed rule survives the class count.</b> Stepping the hue
    /// by the golden angle spreads what it generates and knows nothing of the seven fixed
    /// colours it follows — measured, the 47th class landed 7 units in RGB from the palette's
    /// purple. Nudging the angle when that happens only moves the collision: at 256 classes the
    /// nudges collide with each other and two came out identical.
    /// </para>
    /// <para>
    /// <b>So the choice is made against what is actually taken.</b> A grid of 720 colours — 60
    /// hues by four lightnesses by three saturations — and each class takes the one whose
    /// nearest neighbour among the chosen is furthest away. That is the standard
    /// furthest-point rule, it needs no threshold to tune, and it degrades honestly: with more
    /// classes the guaranteed gap narrows, because there is nowhere for it not to.
    /// </para>
    /// <para>
    /// <b>Measured spread</b> — the closest pair in RGB, out of a cube whose diagonal is 441:
    /// <b>8 and 20 classes 77.9 apart</b>, <b>81 classes 49</b>, <b>256 classes 23</b>. The
    /// golden-angle rule it replaced gave <b>7</b> at eighty-one, and two identical colours at
    /// two hundred and fifty-six.
    /// </para>
    /// </remarks>
    /// <param name="chosen">What is already taken, which the spread works around.</param>
    /// <param name="classes">How many are needed in total.</param>
    /// <returns>The full list.</returns>
    private static List<Rgba> Spread(List<Rgba> chosen, int classes)
    {
        List<Rgba> candidates = [];

        for (int hue = 0; hue < 360; hue += 6)
        {
            foreach (double light in (double[])[0.35, 0.5, 0.65, 0.8])
            {
                foreach (double saturation in (double[])[0.45, 0.7, 0.95])
                {
                    candidates.Add(FromHsl(hue, saturation, light));
                }
            }
        }

        while (chosen.Count < classes && candidates.Count > 0)
        {
            int best = 0;
            double furthest = -1;

            for (int i = 0; i < candidates.Count; i++)
            {
                double apart = Apart(candidates[i], chosen);

                if (apart > furthest)
                {
                    furthest = apart;
                    best = i;
                }
            }

            chosen.Add(candidates[best]);
            candidates.RemoveAt(best);
        }

        return chosen;
    }

    /// <summary>How far a candidate is from the nearest colour already chosen.</summary>
    /// <param name="candidate">The colour being considered.</param>
    /// <param name="chosen">What is already taken.</param>
    /// <returns>The distance to the nearest, or the whole cube when nothing is taken.</returns>
    private static double Apart(Rgba candidate, List<Rgba> chosen)
    {
        double nearest = double.MaxValue;

        foreach (Rgba one in chosen)
        {
            double apart = Math.Sqrt(
                Math.Pow(candidate.R - one.R, 2)
                + Math.Pow(candidate.G - one.G, 2)
                + Math.Pow(candidate.B - one.B, 2));

            nearest = Math.Min(nearest, apart);
        }

        return nearest;
    }

    /// <summary>A colour from hue, saturation and lightness.</summary>
    /// <remarks>
    /// <b>Written out rather than pulled in.</b> The conversion is eight lines and entirely
    /// specified; a dependency for it would be a dependency to keep.
    /// </remarks>
    /// <param name="hue">Degrees, 0 to 360.</param>
    /// <param name="saturation">0 to 1.</param>
    /// <param name="lightness">0 to 1.</param>
    /// <returns>The colour, fully opaque.</returns>
    private static Rgba FromHsl(double hue, double saturation, double lightness)
    {
        double chroma = (1 - Math.Abs((2 * lightness) - 1)) * saturation;
        double sector = hue / 60;
        double second = chroma * (1 - Math.Abs((sector % 2) - 1));
        double lift = lightness - (chroma / 2);

        (double red, double green, double blue) = (int)sector switch
        {
            0 => (chroma, second, 0.0),
            1 => (second, chroma, 0.0),
            2 => (0.0, chroma, second),
            3 => (0.0, second, chroma),
            4 => (second, 0.0, chroma),
            _ => (chroma, 0.0, second),
        };

        return new Rgba(
            (byte)Math.Clamp(Math.Round((red + lift) * 255), 0, 255),
            (byte)Math.Clamp(Math.Round((green + lift) * 255), 0, 255),
            (byte)Math.Clamp(Math.Round((blue + lift) * 255), 0, 255),
            255);
    }

    /// <summary>What a legend calls one class.</summary>
    private static string Label(double from, double to) => string.Create(
        CultureInfo.InvariantCulture,
        $"{Trim(from)} - {Trim(to)}");

    /// <summary>A bound as a legend should print it.</summary>
    /// <remarks>
    /// <b>Six significant figures, then the trailing zeros come off.</b> A quantile bound is
    /// 1234.5678901234 and no legend has room for that; rounding to the number's own scale keeps
    /// an integer field's classes reading as integers.
    /// </remarks>
    private static string Trim(double value)
    {
        double rounded = Math.Round(value, 6);

        return rounded == Math.Floor(rounded) && Math.Abs(rounded) < 1e15
            ? ((long)rounded).ToString(CultureInfo.InvariantCulture)
            : rounded.ToString("0.######", CultureInfo.InvariantCulture);
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    /// <summary>A number from a node, whatever kind of number it was written as.</summary>
    /// <remarks>
    /// <b>`TryGetValue&lt;double&gt;` refuses a `JsonValue` created from an `int`, and this
    /// project has now been caught by that twice.</b> The first time it silently produced the
    /// wrong stop in a style; here it silently ignored `breakCount` and every classification came
    /// back with the default five classes -- a plausible answer to a question nobody asked.
    /// Going through the element's own kind reads both.
    /// </remarks>
    /// <param name="node">The node.</param>
    /// <returns>The number, or null when it is not one.</returns>
    private static double? Number(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue(out double number))
        {
            return number;
        }

        return value.TryGetValue(out int whole) ? whole
            : value.TryGetValue(out long big) ? big
            : null;
    }

    private static Task RefuseAsync(HttpContext context, string why) =>
        Results.Json(
            new { error = new { code = 400, message = why } },
            statusCode: StatusCodes.Status400BadRequest)
            .ExecuteAsync(context);
}
