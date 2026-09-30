using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Features;
using Graticula.Formats;
using Graticula.Platform.Catalog;

namespace Graticula.Host;

/// <summary>
/// A layer's rows written out as a file the import reader or a client can read — the one place both export routes
/// read a layer whole.
/// </summary>
/// <remarks>
/// <para>
/// <b>Moved out of <see cref="LayerExportEndpoints"/> when ADR-106's job needed it, and not copied.</b> The synchronous
/// route (ADR-107) and <see cref="FeatureExporter"/> must read a layer the same way — the served field list, the layer's
/// own reference, the identity ordering, the one row past the cap — or a layer would come out of one route differently
/// from the other, and the difference would be found by whoever compared two files.
/// </para>
/// <para>
/// <b>The row past the cap is the proof there is more.</b> Every writer here reads one row beyond what it was allowed
/// and stops, so the caller can tell <i>exactly the cap</i> from <i>more than the cap</i> and refuse the second rather
/// than hand over a layer that was silently cut.
/// </para>
/// </remarks>
internal static class LayerExportRows
{
    /// <summary>
    /// Writes rows, page by page, as GeoJSON in the layer's own reference — the reader's input; returns how many were
    /// read, which is at most <c>most + 1</c>.
    /// </summary>
    /// <param name="output">Where the GeoJSON goes.</param>
    /// <param name="source">The layer's source, as <see cref="ServiceContexts.GetAsync"/> gives it.</param>
    /// <param name="described">The served description, whose fields are the ones read.</param>
    /// <param name="layer">The layer.</param>
    /// <param name="srid">The reference the coordinates are written in, named in the file's <c>crs</c> member.</param>
    /// <param name="most">The most rows the caller will keep; one more is read.</param>
    /// <param name="pageSize">Rows per query; <see cref="FeatureQuery.MaximumLimit"/> for a whole layer, fewer to sample.</param>
    /// <param name="progress">Told the running count after each page, or null.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The rows written.</returns>
    /// <remarks>
    /// <b>CRS84 for WGS 84, so GDAL reads longitude first; the EPSG code otherwise.</b> The legacy <c>crs</c> member is
    /// what GDAL reads a GeoJSON file's reference from, measured for 3857 and 5254 (ADR-107 §7).
    /// </remarks>
    internal static async Task<long> WriteGeoJsonAsync(
        Stream output,
        IFeatureSource source,
        LayerDescription described,
        PublishedLayer layer,
        int srid,
        long most,
        int pageSize,
        Func<long, CancellationToken, Task>? progress,
        CancellationToken cancellation)
    {
        string identity = layer.Definition.IdentityColumn;
        long count = 0;

        // Every column the layer publishes, named: a query that names none answers with the identity alone.
        List<string> fields = [.. described.Fields.Select(f => f.Name)];

        await using Utf8JsonWriter json = new(output);

        json.WriteStartObject();
        json.WriteString("type", "FeatureCollection");

        json.WriteStartObject("crs");
        json.WriteString("type", "name");
        json.WriteStartObject("properties");
        json.WriteString("name", srid == 4326 ? "urn:ogc:def:crs:OGC:1.3:CRS84" : $"EPSG:{srid}");
        json.WriteEndObject();
        json.WriteEndObject();

        json.WriteStartArray("features");

        for (int offset = 0; count <= most; offset += pageSize)
        {
            int page = 0;

            await foreach (Feature feature in source.ReadAsync(
                new FeatureQuery(pageSize, fields: fields, offset: offset, orderBy: [new SortKey(identity, false)], outSrid: srid),
                cancellation).ConfigureAwait(false))
            {
                page++;
                count++;

                json.WriteStartObject();
                json.WriteString("type", "Feature");

                if (feature.Geometry is { } geometry)
                {
                    json.WritePropertyName("geometry");
                    GeoJsonWriter.WriteGeometry(json, geometry);
                }
                else
                {
                    json.WriteNull("geometry");
                }

                json.WriteStartObject("properties");

                for (int i = 0; i < feature.Schema.Count; i++)
                {
                    json.WritePropertyName(feature.Schema.Names[i]);
                    GeoJsonWriter.WriteValue(json, feature[i]);
                }

                json.WriteEndObject();
                json.WriteEndObject();

                if (json.BytesPending > 1 << 16) await json.FlushAsync(cancellation).ConfigureAwait(false);

                // The one row past the cap has been read: there is more, and reading on would only spend the source.
                if (count > most) break;
            }

            if (progress is not null) await progress(count, cancellation).ConfigureAwait(false);

            if (page < pageSize) break;
        }

        json.WriteEndArray();
        json.WriteEndObject();
        await json.FlushAsync(cancellation).ConfigureAwait(false);

        return count;
    }

    /// <summary>
    /// Writes the layer as one Esri JSON FeatureSet, the query writer's own — ADR-106's Feature Collection; returns how
    /// many rows, at most <c>most + 1</c>.
    /// </summary>
    /// <remarks>
    /// <b>Written by the query writer</b>, so its fields, types, aliases, object ids and dates are exactly what
    /// <c>query?f=json</c> answers, in the layer's own reference; <see cref="WholeLayerSource"/> hands it every page as
    /// one read, and the writer leaves <c>exceededTransferLimit</c> out because a file has no next page.
    /// </remarks>
    internal static async Task<long> WriteEsriJsonAsync(
        Stream output,
        PublishedLayer layer,
        IFeatureSource source,
        LayerDescription described,
        long most,
        CancellationToken cancellation)
    {
        string identity = layer.Definition.IdentityColumn;
        int srid = layer.Definition.Srid;
        List<string> fields = [.. described.Fields.Select(f => f.Name)];

        FeatureQuery Page(int offset) =>
            new(FeatureQuery.MaximumLimit, fields: fields, offset: offset, orderBy: [new SortKey(identity, false)], outSrid: srid);

        await using Utf8JsonWriter json = new(output);

        return await new Graticula.Api.ArcGis.FeatureServerQueryWriter(
                layer.Definition, 0, described.Fields, geoJson: false, whole: true)
            .WriteAsync(json, new WholeLayerSource(source, Page, most), Page(0), layer.GeometryType, cancellation)
            .ConfigureAwait(false);
    }

    /// <summary>A file name made of the layer's name, safe on every file system.</summary>
    internal static string SafeName(string name)
    {
        char[] bad = Path.GetInvalidFileNameChars();
        string safe = new([.. name.Select(c => bad.Contains(c) || c == ' ' ? '_' : c)]);
        return string.IsNullOrWhiteSpace(safe) ? "layer" : safe;
    }
}
