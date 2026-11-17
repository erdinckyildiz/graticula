using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.ArcGis;
using Graticula.Cartography;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Host.Forms.Reports;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace Graticula.Host.Forms;

/// <summary>
/// What a feature report reads beside its records — ADR-301 §5.10 (maps) and §5.11 (summary sections and queries): asked of
/// the form's own layers through their query, and drawn by the print service's renderer, always as the asker.
/// </summary>
public static partial class FormEndpoints
{
    /// <summary>The header a report says what it could not draw in full in, percent-encoded.</summary>
    public const string ReportNotesHeader = "Graticula-Report-Notes";

    /// <summary>The scale a map of one point is drawn at when no mapScale is written (INFERRED: Survey123 uses the question's).</summary>
    public const double DefaultPointScale = 10_000;

    /// <summary>How many times a report is filled to find what it reads: sections nested in sections need a round each.</summary>
    private const int PrefetchRounds = 6;

    /// <summary>The form's layers as the asker reads them, and how records of one are read by their ids.</summary>
    private sealed record ReportReading(Dictionary<string, ReportLayer> Layers, Func<string, long[], Task<List<ReportRecord>>> Read);

    /// <summary>
    /// Fills the template until nothing it reads is missing: each fill says which maps and queries it needed, which are
    /// fetched as the asker before the next. A report that still misses some after the last round writes them empty and
    /// says so.
    /// </summary>
    private static async Task PrefetchAsync(
        HttpContext context, JsonObject publication, ReportSchema schema, ReportTemplate template, List<ReportRecord> records, bool merged,
        ReportEvaluator evaluator, ReportPrefetch lookups, ReportReading reading, CancellationToken cancellation)
    {
        for (int round = 0; round < PrefetchRounds; round++)
        {
            lookups.ClearMissing();

            if (merged || records.Count == 1)
            {
                _ = template.Fill(records, evaluator, lookups);
            }
            else
            {
                foreach (ReportRecord record in records)
                {
                    _ = template.Fill([record], evaluator, lookups);
                }
            }

            if (lookups.Complete)
            {
                return;
            }

            foreach (ReportQueryRequest query in lookups.MissingQueries.ToList())
            {
                lookups.Add(query, await ReportQueryAsync(context, schema, reading, query, cancellation).ConfigureAwait(false));
            }

            foreach (ReportMapRequest map in lookups.MissingMaps.ToList())
            {
                if (lookups.MapsDrawn >= MaximumReportMaps)
                {
                    throw new ReportTemplateException($"The report would draw more than {MaximumReportMaps} maps; choose fewer records.");
                }

                lookups.Add(map, await ReportMapAsync(context, publication, reading, map, lookups.Notes, cancellation).ConfigureAwait(false));
            }
        }

        lookups.Notes.Add($"Sections nested more than {PrefetchRounds} deep in queries were written empty.");
    }

    // ------------------------------------------------------------------------------------------------------- queries

    /// <summary>
    /// Translates a clause written in the form's question names into the layer's columns: each name outside quotes that
    /// is a question of the table becomes its column. The clause is then read by the layer's own where-clause reader.
    /// </summary>
    private static string InColumns(string text, Dictionary<string, string> columns)
    {
        StringBuilder written = new();
        char quote = '\0';

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (quote != '\0')
            {
                written.Append(c);
                quote = c == quote ? '\0' : quote;
                continue;
            }

            if (c is '\'' or '"')
            {
                quote = c;
                written.Append(c);
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                int start = i;

                while (i + 1 < text.Length && (char.IsLetterOrDigit(text[i + 1]) || text[i + 1] == '_'))
                {
                    i++;
                }

                string word = text[start..(i + 1)];
                written.Append(columns.TryGetValue(word, out string? column) ? column : word);
                continue;
            }

            written.Append(c);
        }

        return written.ToString();
    }

    /// <summary>A value as a report writes it: a database's number as a double, so an average is not written to 28 digits.</summary>
    private static object? Normalised(object? value) => value switch
    {
        decimal m => (double)m,
        float f => (double)f,
        int i => (double)i,
        long l => (double)l,
        short s => (double)s,
        _ => value,
    };

    /// <summary>
    /// Answers one query of a report through the layer's own query, as the asker: <c>where</c> (with the records the
    /// report is made of, unless <c>!important</c>), <c>stats</c> as <c>outStatistics</c>, <c>orderByFields</c>,
    /// <c>resultRecordCount</c>, <c>outFields</c> with <c>returnDistinctValues</c>.
    /// </summary>
    private static async Task<ReportQueryResult> ReportQueryAsync(
        HttpContext context, ReportSchema schema, ReportReading reading, ReportQueryRequest request, CancellationToken cancellation)
    {
        ReportQueryResult nothing = new([], []);

        if (!reading.Layers.TryGetValue(request.Table, out ReportLayer? layer))
        {
            throw new ReportTemplateException($"The report queries {(request.Table.Length == 0 ? "the form's layer" : $"the repeat '{request.Table}'")}, which you may not read.");
        }

        FormTable table = request.Table.Length == 0 ? schema.Root
            : schema.Root.AllTables.First(t => string.Equals(t.Name, request.Table, StringComparison.OrdinalIgnoreCase));
        JsonObject published = layer.Published["columns"] as JsonObject ?? [];
        Dictionary<string, string> toColumn = new(StringComparer.OrdinalIgnoreCase);

        foreach (FormQuestion q in table.Questions)
        {
            if ((string?)published[q.Name] is { } column)
            {
                toColumn[q.Name] = column;
            }
        }

        Dictionary<string, string> toQuestion = toColumn.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);
        string identity = layer.Layer.Definition.IntegerIdentityColumn ?? layer.Layer.Definition.IdentityColumn;
        List<string> clauses = [];

        if (request.Where is { } where)
        {
            clauses.Add("(" + InColumns(where, toColumn) + ")");
        }

        if (request.WithinIds is { } ids)
        {
            if (ids.Count == 0)
            {
                return nothing;
            }

            clauses.Add($"{identity} IN ({string.Join(", ", ids.Select(i => i.ToString(CultureInfo.InvariantCulture)))})");
        }

        if (request.WithinParents is { } parents)
        {
            if (parents.Count == 0)
            {
                return nothing;
            }

            clauses.Add($"{FormLayerPlan.ParentColumn} IN ({string.Join(", ", parents.Select(p => $"'{p.ToString("B").ToUpperInvariant()}'"))})");
        }

        Dictionary<string, StringValues> asked = new(StringComparer.OrdinalIgnoreCase)
        {
            ["where"] = clauses.Count == 0 ? "1=1" : string.Join(" AND ", clauses),
            ["returnGeometry"] = "false",
        };

        if (request.OrderBy is { Length: > 0 } order)
        {
            asked["orderByFields"] = InColumns(order, toColumn);
        }

        if (request.Count is { } count)
        {
            asked["resultRecordCount"] = count.ToString(CultureInfo.InvariantCulture);
        }

        // The statistics this server's query computes; Survey123's range is max − min, median the 0.5 percentile, and
        // first and last are read as rows (INFERRED: in object id order).
        List<ReportStatistic> native = [];
        List<ReportStatistic> ordered = [];

        foreach (ReportStatistic s in request.Statistics)
        {
            switch (s.Type)
            {
                case "range":
                    native.Add(s with { Type = "min", OutName = "graticula_min_" + s.OutName });
                    native.Add(s with { Type = "max", OutName = "graticula_max_" + s.OutName });
                    break;
                case "first" or "last":
                    ordered.Add(s);
                    break;
                default:
                    native.Add(s);
                    break;
            }
        }

        if (native.Count > 0)
        {
            asked["outStatistics"] = new JsonArray([.. native.Select(s => (JsonNode)new JsonObject
            {
                ["statisticType"] = s.Type == "median" ? "percentile_cont" : s.Type,
                ["onStatisticField"] = s.Field == "*" ? "*" : toColumn.GetValueOrDefault(s.Field, s.Field),
                ["outStatisticFieldName"] = s.OutName,
                ["statisticParameters"] = s.Type == "median" ? new JsonObject { ["value"] = 0.5 } : null,
            })]).ToJsonString();

            if (request.GroupBy is { Length: > 0 } groups)
            {
                asked["groupByFieldsForStatistics"] = InColumns(groups, toColumn);
            }

            if (request.Having is { Length: > 0 } having)
            {
                asked["havingClause"] = InColumns(having, toColumn);
            }
        }
        else if (ordered.Count == 0 && request.Distinct && request.OutFields is { Length: > 0 } outFields)
        {
            asked["outFields"] = InColumns(outFields, toColumn);
            asked["returnDistinctValues"] = "true";
        }
        else if (ordered.Count == 0)
        {
            asked["outFields"] = identity;
        }

        if (native.Count == 0 && ordered.Count > 0)
        {
            Dictionary<string, object?> row = new(StringComparer.OrdinalIgnoreCase);

            foreach (ReportStatistic s in ordered)
            {
                row[s.OutName] = await FirstOrLastAsync(s).ConfigureAwait(false);
            }

            return new ReportQueryResult([], [row]);
        }

        FeatureQuery query = Parse(asked);
        BudgetedFeatureSource? budgeted = layer.Source as BudgetedFeatureSource;
        IFeatureSource source = budgeted?.Inner ?? layer.Source;

        if (native.Count > 0)
        {
            IReadOnlyList<IReadOnlyDictionary<string, object?>> rows;

            using (ConnectionBudget.Lease lease = budgeted is not null ? await budgeted.LeaseAsync(cancellation).ConfigureAwait(false) : default)
            {
                if (source is not IFeatureSummaries summaries)
                {
                    throw new ReportTemplateException("The form's layer does not compute statistics on this server, so '| stats' cannot be answered.");
                }

                rows = await summaries.StatisticsAsync(query, cancellation).ConfigureAwait(false);
            }

            List<IReadOnlyDictionary<string, object?>> written = [];

            foreach (IReadOnlyDictionary<string, object?> row in rows.Take(MaximumReportRows))
            {
                Dictionary<string, object?> named = new(StringComparer.OrdinalIgnoreCase);

                foreach ((string key, object? value) in row)
                {
                    named[toQuestion.GetValueOrDefault(key, key)] = Normalised(value);
                }

                foreach (ReportStatistic s in request.Statistics.Where(s => s.Type == "range"))
                {
                    named[s.OutName] = ReportValue.AsNumber(named.GetValueOrDefault("graticula_max_" + s.OutName)) is { } max
                        && ReportValue.AsNumber(named.GetValueOrDefault("graticula_min_" + s.OutName)) is { } min ? max - min : null;
                }

                foreach (ReportStatistic s in ordered)
                {
                    named[s.OutName] = await FirstOrLastAsync(s).ConfigureAwait(false);
                }

                written.Add(named);
            }

            return new ReportQueryResult([], written);
        }

        List<Feature> features = [];

        await foreach (Feature f in layer.Source.ReadAsync(query, cancellation).ConfigureAwait(false))
        {
            features.Add(f);

            if (features.Count >= MaximumReportRows)
            {
                break;
            }
        }

        if (request.AnswersRows)
        {
            List<IReadOnlyDictionary<string, object?>> distinct = [];

            foreach (Feature f in features)
            {
                Dictionary<string, object?> named = new(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < f.Schema.Count; i++)
                {
                    named[toQuestion.GetValueOrDefault(f.Schema.Names[i], f.Schema.Names[i])] = Normalised(f[i]);
                }

                distinct.Add(named);
            }

            return new ReportQueryResult([], distinct);
        }

        long[] found = [.. features.Select(f => Convert.ToInt64(f[f.Schema.IndexOf(identity)], CultureInfo.InvariantCulture))];
        return new ReportQueryResult(await reading.Read(request.Table, found).ConfigureAwait(false), []);

        FeatureQuery Parse(Dictionary<string, StringValues> parameters)
        {
            HostSettings settings = context.RequestServices.GetRequiredService<HostSettings>();

            if (!FeatureServerQueryParameters.TryParse(
                    new QueryCollection(parameters), identity, layer.Layer.Definition.Srid, layer.Described.Fields,
                    out FeatureQuery? parsed, out _, out string? error,
                    serverDefaultRecordCount: MaximumReportRows, serverMaximumRecordCount: Math.Min(MaximumReportRows, settings.MaximumRecordCount)))
            {
                throw new ReportTemplateException($"A query in the template could not be asked of {(request.Table.Length == 0 ? "the form's layer" : $"the repeat '{request.Table}'")}: {error}");
            }

            return parsed;
        }

        async Task<object?> FirstOrLastAsync(ReportStatistic s)
        {
            string column = toColumn.GetValueOrDefault(s.Field, s.Field);
            Dictionary<string, StringValues> one = new(asked, StringComparer.OrdinalIgnoreCase)
            {
                ["where"] = $"({asked["where"]}) AND {column} IS NOT NULL",
                ["outFields"] = column,
                ["orderByFields"] = $"{identity} {(s.Type == "first" ? "ASC" : "DESC")}",
                ["resultRecordCount"] = "1",
            };
            one.Remove("outStatistics");
            one.Remove("groupByFieldsForStatistics");
            one.Remove("havingClause");
            one.Remove("returnDistinctValues");

            await foreach (Feature f in layer.Source.ReadAsync(Parse(one), cancellation).ConfigureAwait(false))
            {
                return Normalised(f[f.Schema.IndexOf(column)]);
            }

            return null;
        }
    }

    // ---------------------------------------------------------------------------------------------------------- maps

    /// <summary>
    /// Draws one map of a report with the print service's renderer (ADR-265), as the asker: the web map named, or the
    /// organisation's default basemap (ADR-086), the records' layer with its own symbols when <c>drawingInfo</c> asks, and
    /// the records' geometries marked on top. What could not be drawn is said in <paramref name="notes"/>; a map that
    /// cannot be drawn at all is written empty, never failing the report.
    /// </summary>
    private static async Task<ReportMap> ReportMapAsync(
        HttpContext context, JsonObject publication, ReportReading reading, ReportMapRequest request, SortedSet<string> notes, CancellationToken cancellation)
    {
        IProjector projector = context.RequestServices.GetRequiredService<IProjector>();
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;
        JsonArray operational = [];
        JsonArray ground = [];
        int srid = 3857;
        JsonObject? initial = null;
        bool fromWebMap = false;

        if (request.WebMap is { } id)
        {
            // The web map as the asker may open it: absent and not shared are one sentence.
            if (WebMaps.IsId(id)
                && await context.RequestServices.GetRequiredService<IWebMapStore>().FindAsync(id, cancellation).ConfigureAwait(false) is { Document: { Length: > 0 } content } map
                && LayerAccess.Evaluate(map.Sharing, map.Owner, current.Principal, current.Authorization, map.SharedWith).IsAllowed())
            {
                try
                {
                    if (JsonNode.Parse(content) is JsonObject webMap)
                    {
                        operational = webMap["operationalLayers"] is JsonArray layers ? (JsonArray)layers.DeepClone() : [];
                        ground = webMap["baseMap"]?["baseMapLayers"] is JsonArray basemap ? (JsonArray)basemap.DeepClone() : [];
                        srid = webMap["spatialReference"]?["wkid"] is JsonValue wkid && wkid.TryGetValue(out int w) ? w
                            : webMap["spatialReference"]?["latestWkid"] is JsonValue latest && latest.TryGetValue(out int l) ? l : 3857;
                        initial = webMap["initialState"]?["viewpoint"]?["targetGeometry"] as JsonObject;
                        fromWebMap = true;
                    }
                }
                catch (JsonException)
                {
                    fromWebMap = false;
                }
            }

            if (!fromWebMap)
            {
                notes.Add($"The web map {id} that a map in the template names is not there for you to read, so the map was drawn on the organisation's default basemap.");
            }
        }

        if (!fromWebMap)
        {
            // ADR-086: the organisation's default basemap, as this caller may draw it (INFERRED for a report's map).
            if (JsonSerializer.SerializeToNode(await PortalEndpoints.DefaultBasemapAsync(context, current, cancellation).ConfigureAwait(false))
                ?["baseMapLayers"] is JsonArray basemap)
            {
                ground = (JsonArray)basemap.DeepClone();
            }
        }

        if (request.CurrentLayer && reading.Layers.TryGetValue(request.Table, out ReportLayer? own) && request.ObjectIds.Count > 0)
        {
            string identity = own.Layer.Definition.IntegerIdentityColumn ?? own.Layer.Definition.IdentityColumn;
            string path = Uri.TryCreate(own.Url, UriKind.Absolute, out Uri? absolute) ? absolute.AbsolutePath : own.Url;
            operational.Add(new JsonObject
            {
                ["id"] = "graticula-report-records",
                ["title"] = own.Layer.DisplayName,
                ["url"] = path,
                ["layerType"] = "ArcGISFeatureLayer",
                ["visibility"] = true,
                ["layerDefinition"] = new JsonObject
                {
                    ["definitionExpression"] = $"{identity} IN ({string.Join(", ", request.ObjectIds.Select(i => i.ToString(CultureInfo.InvariantCulture)))})",
                },
            });
        }

        // The extent: mapExtent as written; else the records' geometries — one point at the scale, several fitted; else the
        // web map's own; else the world.
        double? scale = request.Scale > 0 ? request.Scale : null;
        JsonObject extent;

        if (request.Extent is { } written)
        {
            extent = Extent(written[0], written[1], written[2], written[3], (int)written[4]);
        }
        else if (request.Shapes.Count > 0)
        {
            Envelope around = request.Shapes.Select(s => s.Envelope).Aggregate((a, b) => a.Union(b));
            Envelope? carried = srid == 4326 ? around
                : around.Width <= 0 && around.Height <= 0 ? await PointAsync(around.MinX, around.MinY).ConfigureAwait(false)
                : await ImageServerEndpoints.ProjectEnvelopeAsync(around, 4326, srid, projector, cancellation).ConfigureAwait(false);

            if (carried is not { } box)
            {
                notes.Add($"A map's records could not be carried into EPSG:{srid.ToString(CultureInfo.InvariantCulture)}, so it was not drawn.");
                return new ReportMap([]);
            }

            if (box.Width <= 0 && box.Height <= 0)
            {
                scale ??= DefaultPointScale;
                double tiny = AxisOrder.IsGeographic(srid) ? 1e-6 : 0.1;
                extent = Extent(box.MinX - tiny, box.MinY - tiny, box.MaxX + tiny, box.MaxY + tiny, srid);
            }
            else
            {
                // A tenth of the records' extent around them, and at least as much as a point's map would show (INFERRED).
                double least = AxisOrder.IsGeographic(srid) ? 0.001 : 100;
                double padX = Math.Max(box.Width * 0.1, least), padY = Math.Max(box.Height * 0.1, least);
                extent = Extent(box.MinX - padX, box.MinY - padY, box.MaxX + padX, box.MaxY + padY, srid);
            }
        }
        else if (initial is not null && initial["xmin"] is not null)
        {
            extent = (JsonObject)initial.DeepClone();
            extent["spatialReference"] ??= new JsonObject { ["wkid"] = srid };
        }
        else
        {
            extent = Extent(-180, -80, 180, 80, 4326);
        }

        JsonObject print = new()
        {
            ["mapOptions"] = new JsonObject
            {
                ["extent"] = extent,
                ["spatialReference"] = new JsonObject { ["wkid"] = srid },
                ["scale"] = scale,
            },
            ["operationalLayers"] = operational,
            ["baseMap"] = new JsonObject { ["baseMapLayers"] = ground },
            ["exportOptions"] = new JsonObject { ["outputSize"] = new JsonArray(request.Width, request.Height), ["dpi"] = 96 },
        };

        if (!WebMapPrintRequest.TryParse(print.ToJsonString(), out WebMapPrintRequest? parsed, out string? unreadable))
        {
            notes.Add($"A map in the report was not drawn: {unreadable}");
            return new ReportMap([]);
        }

        IReadOnlyList<Geometry> marked = [];

        if (!request.CurrentLayer && request.Shapes.Count > 0)
        {
            try
            {
                marked = srid == 4326 ? request.Shapes
                    : (await projector.ProjectAsync([.. request.Shapes], 4326, srid, cancellation).ConfigureAwait(false)).Projected;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                notes.Add($"A map's records could not be carried into EPSG:{srid.ToString(CultureInfo.InvariantCulture)}, so they are not marked on it.");
            }
        }

        (byte[]? png, string? refused, List<string> left) = await PrintEndpoints.DrawMapImageAsync(
            context, parsed!, (canvas, transform) => Mark(canvas, transform, marked), cancellation).ConfigureAwait(false);

        foreach (string note in left)
        {
            notes.Add($"A map in the report: {note}");
        }

        if (png is null)
        {
            notes.Add($"A map in the report was not drawn: {refused}");
            return new ReportMap([]);
        }

        return new ReportMap(png);

        // One point carried alone: an envelope of no area is not one ProjectEnvelopeAsync carries.
        async Task<Envelope?> PointAsync(double x, double y)
        {
            try
            {
                (IReadOnlyList<Geometry> projected, _) = await projector.ProjectAsync([new Point(x, y)], 4326, srid, cancellation).ConfigureAwait(false);
                return projected.Count == 1 && projected[0] is Point { IsEmpty: false } p && double.IsFinite(p.X) && double.IsFinite(p.Y)
                    ? new Envelope(p.X, p.Y, p.X, p.Y) : null;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return null;
            }
        }

        static JsonObject Extent(double xmin, double ymin, double xmax, double ymax, int wkid) => new()
        {
            ["xmin"] = xmin, ["ymin"] = ymin, ["xmax"] = xmax, ["ymax"] = ymax,
            ["spatialReference"] = new JsonObject { ["wkid"] = wkid },
        };
    }

    /// <summary>The colour a report's map marks its records in (INFERRED; Survey123 draws "a default symbol").</summary>
    internal static readonly Rgba ReportMarkColour = new(226, 37, 43, 255);

    /// <summary>Marks geometries on a drawn map: a point as a dot ringed in white, a line as a stroke, an area filled and outlined.</summary>
    internal static void Mark(IMapCanvas canvas, PixelTransform transform, IReadOnlyList<Geometry> shapes)
    {
        MapSymbol.Marker dot = new(ReportMarkColour, 7, Rgba.White, 2);
        MapSymbol.Stroke line = new(ReportMarkColour, 3, null);
        MapSymbol.Area area = new(ReportMarkColour with { A = 70 }, ReportMarkColour, 2.5);

        foreach (Geometry shape in shapes)
        {
            Draw(shape);
        }

        void Draw(Geometry shape)
        {
            switch (shape)
            {
                case Point p:
                    canvas.DrawMarker(transform.X(p.X), transform.Y(p.Y), dot);
                    break;
                case MultiPoint many:
                    foreach (Point p in many.Parts)
                    {
                        Draw(p);
                    }

                    break;
                case Polygon polygon:
                    PixelPath rings = new();
                    Ring(rings, polygon.Shell.Coordinates, closed: true);

                    foreach (LinearRing hole in polygon.Holes)
                    {
                        Ring(rings, hole.Coordinates, closed: true);
                    }

                    canvas.FillArea(rings, area);
                    break;
                case MultiPolygon polygons:
                    foreach (Polygon p in polygons.Parts)
                    {
                        Draw(p);
                    }

                    break;
                case LineString string1:
                    PixelPath path = new();
                    Ring(path, string1.Coordinates, closed: false);
                    canvas.StrokeLine(path, line);
                    break;
                case MultiLineString lines:
                    foreach (LineString l in lines.Parts)
                    {
                        Draw(l);
                    }

                    break;
                default:
                    break;
            }
        }

        void Ring(PixelPath path, XySequence coordinates, bool closed)
        {
            path.Begin(closed);

            for (int i = 0; i < coordinates.Count; i++)
            {
                path.Add(transform.X(coordinates.X(i)), transform.Y(coordinates.Y(i)));
            }

            path.End();
        }
    }
}
