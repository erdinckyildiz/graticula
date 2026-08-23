using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Formats;
using Graticula.Geometries;

namespace Graticula.Host;

/// <summary>
/// Reads a shapefile in the process that is not serving requests.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-113](../../docs/architecture-debt.md), found by the independent §66 simplicity
/// gate as its disqualifying finding.</b> ADR-037 §5a put GDAL in a child process on
/// 2026-08-18 for a stated reason quoted in `Directory.Packages.props` — it *"removes an
/// untrusted-file parser from the process that serves public requests"* — and 1,094 lines
/// of our own shapefile parser went on running inside it, under a decision taken three days
/// earlier against a rule that no longer held. **Two archive formats from the same
/// untrusted upload were parsed on opposite sides of a boundary drawn on purpose.**
/// </para>
/// <para>
/// <b>Nothing was known to be wrong with the parser, and that is not the point.</b> It is
/// bounded, fuzzed against its own corpus, and has a winding-order repair behind it. What
/// was wrong is that the rule which moved GDAL out was never applied to the parser it was
/// written about.
/// </para>
/// <para>
/// <b>The one thing that had to cross the pipe is the operator's character set</b>, and
/// three ways of sending it were measured before one was chosen. `SHAPE_ENCODING` as a GDAL
/// config option set before the open: no effect. The same name in the process environment
/// at start: works, and is useless — this process is long-lived and shared, so an encoding
/// fixed at start is one encoding for every import, and setting the variable from inside
/// does not work because CPL reads the environment before any of it runs. `ENCODING` as an
/// **open option**: per open, which is the only shape a per-request protocol can use. See
/// `Graticula.Import.Reader`'s own `Open`.
/// </para>
/// <para>
/// <b>What stays on this side: the archive's bounds.</b> <c>BoundedArchive</c> and
/// <c>ShapefileBundle</c> still read the ZIP directory and refuse a bomb, a nested archive
/// or a bundle with two shapefiles in it. Those are our limits on an upload rather than a
/// parse of its contents, and they are the reason the three adversarial archives in the
/// corpus are refused before GDAL is asked — which it also refuses, measured, so the
/// bounds are belt and braces rather than the only guard.
/// </para>
/// <para>
/// <b>Columns are inferred from values, not from the DBF's declared types</b> — the same
/// <see cref="InferredColumn"/> the old parser fed and the GeoJSON path feeds. That is what
/// makes the two paths' schemas comparable rather than merely similar.
/// `ShapefileCorpusTests` carries the old parser's corpus onto this path and asserts what
/// each archive publishes as, including the one expectation that changed.
/// </para>
/// </remarks>
internal static class ShapefileViaReader
{
    /// <summary>
    /// How long the child process is given to read one archive.
    /// </summary>
    /// <remarks>
    /// <b>Generous against the bounds already applied.</b> `ArchiveLimits.ForShapefile`
    /// caps what can arrive and `ImportLimits` caps what can be built from it, so this is a
    /// backstop for a process that has stopped answering rather than a limit on real work.
    /// The host owns the kill: a process that could be trusted to stop on request would not
    /// need to be separate.
    /// </remarks>
    public static readonly TimeSpan Deadline = TimeSpan.FromMinutes(5);

    /// <summary>Reads an archive already written to scratch.</summary>
    /// <param name="reader">The child process.</param>
    /// <param name="archive">Where the upload was kept.</param>
    /// <param name="srid">The reference system, resolved or supplied.</param>
    /// <param name="encoding">
    /// The DBF's character set as the operator named it, or null to let the archive's own
    /// <c>.cpg</c> decide.
    /// </param>
    /// <param name="limits">What may be built from it.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The dataset, or the sentence to refuse with.</returns>
    public static async Task<(ImportedDataset? Dataset, bool DroppedZorM, string? Error)>
        ReadAsync(
        GeodatabaseReader reader,
        string archive,
        int srid,
        string? encoding,
        ImportLimits limits,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentException.ThrowIfNullOrWhiteSpace(archive);

        if (!reader.Available)
        {
            return (null, false, "This deployment did not ship the import reader, so a "
                + "shapefile cannot be read. GeoJSON is unaffected.");
        }

        string? layerName;
        GeometryKind? kind;

        using (JsonDocument listed = await reader.AskAsync(
            new { op = "layers", archive, encoding },
            Deadline,
            cancellation).ConfigureAwait(false))
        {
            if (!Ok(listed.RootElement, out string? listError))
            {
                return (null, false, listError);
            }

            if (!Single(listed.RootElement, out layerName, out kind, out string? whichError))
            {
                return (null, false, whichError);
            }
        }

        List<ImportedFeature> features = [];
        Dictionary<string, InferredColumn> columns = new(StringComparer.Ordinal);
        List<string> order = [];
        long vertices = 0;
        bool dropped = false;
        GeometryKind? seen = null;
        string? refusal = null;

        (JsonDocument? header, JsonDocument? trailer) = await reader.StreamAsync(
            new { op = "features", archive, layer = layerName, encoding },
            Deadline,
            line =>
            {
                if (refusal is not null)
                {
                    return;
                }

                JsonElement row = line.RootElement;

                if (!row.TryGetProperty("v", out JsonElement values))
                {
                    return;
                }

                if (features.Count >= limits.Features)
                {
                    refusal =
                        $"This shapefile holds more than {limits.Features.ToString("N0", CultureInfo.InvariantCulture)} "
                        + "features, which is the import limit. Split it, or load it into the "
                        + "database directly and register the table.";

                    return;
                }

                Geometry? geometry = null;

                if (row.TryGetProperty("g", out JsonElement shape)
                    && shape.ValueKind == JsonValueKind.String)
                {
                    try
                    {
                        // <b>The dropped-ordinates flag replaces a second pass over the
                        // .shp header.</b> The old path asked `ShapefileReader.DropsZOrM`
                        // after reading, which meant parsing the file's header twice; the
                        // reader answers it per geometry, which is also more precise — a
                        // file whose header says z and whose geometries carry none no
                        // longer produces a warning about data that is not there.
                        geometry = WkbReader.Read(
                            Convert.FromBase64String(shape.GetString() ?? string.Empty),
                            out bool droppedHere);

                        dropped |= droppedHere;
                    }
                    catch (Exception broken)
                        when (broken is WkbFormatException or FormatException
                            or ArgumentException)
                    {
                        refusal =
                            "A geometry in this shapefile could not be read: "
                            + broken.Message;

                        return;
                    }
                }

                /*
                  <b>The layer's type is what the features turn out to be, not what the
                  header declares — and this was found by the database refusing an
                  import.</b> `Geometry type (MultiLineString) does not match column type
                  (LineString)`: GDAL reports a shapefile polyline layer as `wkbLineString`
                  and emits a `MultiLineString` for a record with more than one part,
                  because that is what a shapefile polyline is. Three of eight corpus
                  archives failed that way.

                  <b>So the widest kind seen wins</b>, which is what
                  `ImportedDataset.GeometryType` means: *the single geometry type the layer
                  will hold*. A file of single-part lines publishes as LineString and one
                  with any multi-part record publishes as MultiLineString, which is the
                  narrowest column that holds every row.
                */
                if (geometry is not null)
                {
                    seen = Widest(seen, geometry.Kind);
                }

                // <b>The model's own count, not one written here.</b> `Geometry` knows
                // how many coordinates it holds and a second implementation is a second
                // answer to the same question — which is what a hole in a polygon or a
                // part of a multi-geometry would be counted differently by.
                vertices += geometry?.CoordinateCount ?? 0;

                if (vertices > limits.Vertices)
                {
                    refusal =
                        $"This shapefile holds more than {limits.Vertices.ToString("N0", CultureInfo.InvariantCulture)} "
                        + "vertices, which is the import limit.";

                    return;
                }

                Dictionary<string, JsonElement> attributes = new(StringComparer.Ordinal);

                foreach (JsonProperty attribute in values.EnumerateObject())
                {
                    if (!columns.TryGetValue(attribute.Name, out InferredColumn? column))
                    {
                        if (columns.Count >= limits.Attributes)
                        {
                            refusal =
                                $"This shapefile has more than {limits.Attributes} attributes, "
                                + "which is the import limit.";

                            return;
                        }

                        column = new InferredColumn { Name = attribute.Name };
                        columns[attribute.Name] = column;
                        order.Add(attribute.Name);
                    }

                    // <b>Cloned, because the document this element belongs to is disposed
                    // when this callback returns.</b> A `JsonElement` is a window onto a
                    // buffer, not a value; keeping one past its document's life reads
                    // whatever landed there next.
                    JsonElement kept = attribute.Value.Clone();

                    column.Observe(kept);
                    attributes[attribute.Name] = kept;
                }

                features.Add(new ImportedFeature(geometry, attributes));
            },
            cancellation).ConfigureAwait(false);

        using (header)
        using (trailer)
        {
            if (refusal is not null)
            {
                return (null, false, refusal);
            }

            if (header is null)
            {
                return (null, false, "The import reader produced no answer.");
            }

            if (!Ok(header.RootElement, out string? headerError))
            {
                return (null, false, headerError);
            }

            if (trailer is null)
            {
                return (null, false,
                    "The import reader stopped part-way through this shapefile.");
            }

            if (!Ok(trailer.RootElement, out string? trailerError))
            {
                return (null, false, trailerError);
            }
        }

        if (features.Count == 0)
        {
            return (
            null, false,
            "This shapefile holds no features, so there is nothing to publish.");
        }

        return (
            new ImportedDataset(
                features,
                [.. Ordered(order, columns)],
                seen ?? kind!.Value,
                srid),
            dropped,
            null);
    }

    /// <summary>
    /// The narrower of two kinds promoted to whichever holds both.
    /// </summary>
    /// <remarks>
    /// <b>Only single-to-multi, and never across families.</b> A file holding both a line
    /// and a polygon is not a layer this server can publish — PostGIS would take a
    /// `GEOMETRY` column and every consumer of the layer document would then have to cope
    /// with a type that changes per row. `ShapefileBundle` cannot produce one either: a
    /// shapefile has one shape type. So the mixed case returns the first kind and the
    /// database refuses the second row, which is a worse message than it could be and is a
    /// case that needs a file this format cannot express.
    /// </remarks>
    private static GeometryKind Widest(GeometryKind? soFar, GeometryKind next)
    {
        if (soFar is not { } already)
        {
            return next;
        }

        if (already == next)
        {
            return already;
        }

        return (already, next) switch
        {
            (GeometryKind.Point, GeometryKind.MultiPoint) => GeometryKind.MultiPoint,
            (GeometryKind.MultiPoint, GeometryKind.Point) => GeometryKind.MultiPoint,
            (GeometryKind.LineString, GeometryKind.MultiLineString) =>
                GeometryKind.MultiLineString,
            (GeometryKind.MultiLineString, GeometryKind.LineString) =>
                GeometryKind.MultiLineString,
            (GeometryKind.Polygon, GeometryKind.MultiPolygon) => GeometryKind.MultiPolygon,
            (GeometryKind.MultiPolygon, GeometryKind.Polygon) => GeometryKind.MultiPolygon,
            _ => already,
        };
    }

    private static IEnumerable<InferredColumn> Ordered(
        List<string> order, Dictionary<string, InferredColumn> columns)
    {
        // <b>In the order the file declared them.</b> A DBF's column order is what an
        // operator sees in ArcGIS, and reordering it makes a published table look like a
        // different file from the one they uploaded.
        foreach (string name in order)
        {
            yield return columns[name];
        }
    }

    /// <summary>Whether the reader said yes, and what to say if not.</summary>
    private static bool Ok(JsonElement answer, out string? error)
    {
        if (answer.TryGetProperty("ok", out JsonElement ok) && ok.ValueKind == JsonValueKind.True)
        {
            error = null;
            return true;
        }

        error = answer.TryGetProperty("error", out JsonElement said)
            ? "This shapefile could not be read: " + said.GetString()
            : "This shapefile could not be read.";

        return false;
    }

    /// <summary>
    /// The one layer a shapefile archive holds, and its geometry.
    /// </summary>
    /// <remarks>
    /// <b><c>ShapefileBundle</c> has already refused an archive with two</b>, so this is a
    /// second check on a thing that cannot happen — kept because it is one comparison and
    /// because the sentence it produces is better than an index out of range.
    /// </remarks>
    private static bool Single(
        JsonElement answer, out string? name, out GeometryKind? kind, out string? error)
    {
        name = null;
        kind = null;
        error = null;

        if (!answer.TryGetProperty("layers", out JsonElement layers)
            || layers.ValueKind != JsonValueKind.Array
            || layers.GetArrayLength() == 0)
        {
            error = "This archive holds no shapefile GDAL can read.";
            return false;
        }

        if (layers.GetArrayLength() > 1)
        {
            error = "This archive holds more than one shapefile. Upload one at a time, so "
                + "that what gets published is the file you meant.";

            return false;
        }

        JsonElement only = layers[0];

        name = only.TryGetProperty("name", out JsonElement said) ? said.GetString() : null;

        if (string.IsNullOrWhiteSpace(name))
        {
            error = "GDAL read this archive and its layer has no name, which this server "
                + "cannot address.";

            return false;
        }

        kind = KindOf(only.TryGetProperty("geometry", out JsonElement shape)
            ? shape.GetString()
            : null);

        if (kind is null)
        {
            error = "This shapefile's geometry type is one this server does not publish. "
                + "Points, lines and polygons are supported, in single and multi form.";

            return false;
        }

        return true;
    }

    /// <summary>
    /// GDAL's geometry-type name as ours.
    /// </summary>
    /// <remarks>
    /// <b>The names are `OGRGeometryTypeToName`'s, and the 25D forms are folded into the
    /// two-dimensional ones.</b> This server's geometry model is two-dimensional and says
    /// so — a z-bearing shapefile publishes as its flat equivalent, with the warning the
    /// endpoint already emits.
    /// </remarks>
    private static GeometryKind? KindOf(string? ogr) => ogr switch
    {
        "wkbPoint" or "wkbPoint25D" => GeometryKind.Point,
        "wkbMultiPoint" or "wkbMultiPoint25D" => GeometryKind.MultiPoint,
        "wkbLineString" or "wkbLineString25D" => GeometryKind.LineString,
        "wkbMultiLineString" or "wkbMultiLineString25D" => GeometryKind.MultiLineString,
        "wkbPolygon" or "wkbPolygon25D" => GeometryKind.Polygon,
        "wkbMultiPolygon" or "wkbMultiPolygon25D" => GeometryKind.MultiPolygon,
        _ => null,
    };
}
