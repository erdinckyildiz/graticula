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
    /// <b>A ceiling, and it refuses rather than truncating.</b> A field with four hundred
    /// distinct values is not a field somebody meant to classify — it is usually an identifier,
    /// or a name — and a renderer with four hundred classes is unreadable and slow on every
    /// face. Truncating would produce a map that looks right and is missing most of its data.
    /// </remarks>
    public const int MostValues = 64;

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

        if (named.Count > 1)
        {
            throw new SymbologyException(
                $"`uniqueValueFields` names {named.Count} fields. This server classifies by one "
                + "(ADR-052 §3.2); combining several would produce classes it could not then "
                + "read back from its own document.");
        }

        string field = Column(named[0], described, "uniqueValueFields");

        // <b>One more than the ceiling, so the ceiling can be enforced rather than guessed.</b>
        // Asking for exactly the limit cannot tell a field with 64 values from one with 6,000.
        FeatureQuery query = new(
            limit: MostValues + 1,
            fields: [field],
            includeGeometry: false,
            distinct: true);

        List<string> values = [];

        await foreach (Feature feature in source.ReadAsync(query, cancellation)
            .ConfigureAwait(false))
        {
            if (feature[field] is { } value)
            {
                values.Add(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
            }
        }

        if (values.Count == 0)
        {
            throw new SymbologyException(
                $"'{field}' has no values in this layer, so there is nothing to classify.");
        }

        if (values.Count > MostValues)
        {
            throw new SymbologyException(
                $"'{field}' has more than {MostValues} distinct values. A renderer with that "
                + "many classes cannot be read on a map or told apart in a legend, and a field "
                + "with that many values is usually an identifier rather than a category.");
        }

        values.Sort(StringComparer.Ordinal);

        JsonObject baseSymbol = Symbol(asked["baseSymbol"], geometry);

        // <b>A qualitative palette, because these classes have no order.</b> The class-breaks
        // path uses a light-to-dark sequential ramp and should: its classes ARE ordered, and the
        // ramp is what says so without a legend. `Block A` and `Block H` are not ordered, and
        // giving them the same ramp tells a reader that H is more of something than A. Found by
        // a design review on 2026-09-04, which noted the two paths had been sharing a ramp by
        // reuse rather than by decision.
        List<Rgba> ramp = asked["colorRamp"] is JsonObject
            ? Ramp(asked["colorRamp"], values.Count)
            : Distinct(values.Count);

        JsonArray infos = [];

        for (int i = 0; i < values.Count; i++)
        {
            infos.Add(new JsonObject
            {
                ["value"] = values[i],
                ["label"] = values[i],
                ["description"] = string.Empty,
                ["symbol"] = Painted(baseSymbol, ramp[i]),
            });
        }

        return new JsonObject
        {
            ["type"] = "uniqueValue",
            ["field1"] = field,
            ["field2"] = null,
            ["field3"] = null,
            ["fieldDelimiter"] = Text(asked["fieldDelimiter"]) ?? ",",
            ["uniqueValueInfos"] = infos,
        };
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
    /// <b>The seven this project already ships</b> — <c>GeneratedSymbology.Palette</c>, chosen to
    /// stay apart from each other and to survive the common colour-blindnesses. Past the seventh
    /// they repeat at three quarters of the lightness, which is the honest thing to do: the
    /// alternative is either inventing hues that collide or refusing an eighth class.
    /// </remarks>
    /// <param name="classes">How many are needed.</param>
    /// <returns>One colour each.</returns>
    private static List<Rgba> Distinct(int classes)
    {
        List<Rgba> colours = [];

        for (int i = 0; i < classes; i++)
        {
            (byte red, byte green, byte blue) =
                GeneratedSymbology.Bytes(
                    GeneratedSymbology.Palette[i % GeneratedSymbology.Palette.Length]);

            int round = i / GeneratedSymbology.Palette.Length;
            double dim = Math.Pow(0.75, round);

            colours.Add(new Rgba(
                (byte)Math.Round(red * dim),
                (byte)Math.Round(green * dim),
                (byte)Math.Round(blue * dim),
                255));
        }

        return colours;
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
