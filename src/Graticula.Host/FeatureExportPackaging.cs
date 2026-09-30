using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Graticula.Platform.Jobs;

namespace Graticula.Host;

/// <summary>
/// What file a feature export becomes and what it is called — ADR-106 §5.6, §5.7: the pure decisions, apart from the
/// disk and the reader so a test can hold each of them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Packaged as ArcGIS Online packages it — owner decision 2026-09-30.</b> GeoPackage, File Geodatabase, KML and
/// Excel hold every chosen layer in <em>one</em> file, a table, feature class, document or sheet each; Shapefile, CSV,
/// GeoJSON and Esri JSON write a file per layer, and several make one <c>.zip</c>. One layer alone is the bare file for
/// CSV, GeoJSON and Esri JSON (ADR-106 §5.6), while a Shapefile is always a zip, because it is already several files.
/// </para>
/// <para>
/// <b>Measured 2026-09-30, and the reason the table below is not a hope.</b> GDAL 3.13's <c>-update -append</c> adds a
/// layer to an existing GeoPackage, OpenFileGDB folder, LIBKML file and XLSX workbook — the four the owner named — each
/// verified by writing two layers of different geometry and reading both back (a table each; a feature class each; a
/// <c>Document</c> each inside the root document; a sheet each). <b>So none of the four falls back to a zip per
/// layer.</b> KML is one <c>Document</c> per layer, which Google Earth lists as a folder, rather than the
/// <c>Folder</c> ADR-106 §5.6 wrote: LIBKML's layer container is the document, and choosing the folder would mean
/// writing the XML ourselves.
/// </para>
/// </remarks>
internal readonly record struct FeatureExportPackaging(
    bool OneDataset, bool Zipped, string Suffix, string MediaType)
{
    /// <summary>The most rows a workbook's sheet holds, less its header row — Microsoft's documented limit.</summary>
    internal const long WorkbookSheetRows = 1_048_575;

    /// <summary>How many characters a worksheet's name may have.</summary>
    internal const int WorkbookSheetName = 31;

    /// <summary>What the export becomes: one dataset or a file per layer, zipped or not, and how it is named.</summary>
    /// <param name="format">The format.</param>
    /// <param name="layers">How many layers are chosen.</param>
    /// <returns>The packaging.</returns>
    internal static FeatureExportPackaging Of(FeatureExportFormat format, int layers)
    {
        bool several = layers > 1;

        return format switch
        {
            FeatureExportFormat.GeoPackage => new(true, false, ".gpkg", "application/geopackage+sqlite3"),
            FeatureExportFormat.Excel => new(true, false, ".xlsx",
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
            FeatureExportFormat.Kml => new(true, false, ".kml", "application/vnd.google-earth.kml+xml"),

            // A File Geodatabase is a folder, so it is a zip however many layers it holds.
            FeatureExportFormat.FileGeodatabase => new(true, true, ".gdb.zip", "application/zip"),

            FeatureExportFormat.Shapefile => new(false, true, "-shapefile.zip", "application/zip"),

            FeatureExportFormat.Csv => several
                ? new(false, true, "-csv.zip", "application/zip")
                : new(false, false, ".csv", "text/csv; charset=utf-8"),
            FeatureExportFormat.GeoJson => several
                ? new(false, true, "-geojson.zip", "application/zip")
                : new(false, false, ".geojson", "application/geo+json"),
            FeatureExportFormat.EsriJson => several
                ? new(false, true, "-featurecollection.zip", "application/zip")
                : new(false, false, ".json", "application/json"),

            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "This format has no packaging."),
        };
    }

    /// <summary>
    /// Whether the reader can add a layer to the file it has already begun, so all the layers land in one — the four
    /// formats ADR-106 says hold every layer in one file.
    /// </summary>
    internal static bool Appends(FeatureExportFormat format) => Of(format, 2).OneDataset;

    /// <summary>The name a download is saved under: the service's name, safe for a file system, and the suffix.</summary>
    /// <param name="service">The service's name.</param>
    /// <param name="format">The format.</param>
    /// <param name="layers">How many layers.</param>
    /// <returns>The file name.</returns>
    internal static string DownloadName(string service, FeatureExportFormat format, int layers)
    {
        StringBuilder name = new();

        foreach (char c in service)
        {
            name.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        }

        return (name.Length == 0 ? "export" : name.ToString()) + Of(format, layers).Suffix;
    }

    /// <summary>The token the reader's <c>export</c> op takes for a format.</summary>
    internal static string ReaderToken(FeatureExportFormat format) => format switch
    {
        FeatureExportFormat.GeoPackage => "gpkg",
        FeatureExportFormat.Shapefile => "shapefile",
        FeatureExportFormat.Excel => "xlsx",
        FeatureExportFormat.FileGeodatabase => "fgdb",
        FeatureExportFormat.Kml => "kml",
        FeatureExportFormat.Csv => "csv",
        FeatureExportFormat.GeoJson => "geojson",
        _ => throw new ArgumentOutOfRangeException(
            nameof(format), format, "The reader writes this format nowhere: Esri JSON is written by the host."),
    };

    /// <summary>
    /// Reads a format from what a caller sent: this server's token, or ArcGIS Online's spelling of it — ADR-106 §5.8.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The format, or null when it is none of them.</returns>
    /// <remarks>
    /// <b>Both vocabularies, because a later <c>content/users/{u}/export</c> maps onto this route one to one.</b>
    /// Case, spaces, dashes and underscores do not matter: <c>File Geodatabase</c>, <c>fileGeodatabase</c> and
    /// <c>file-geodatabase</c> are one word.
    /// </remarks>
    internal static FeatureExportFormat? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string key = new([.. text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);

        return key switch
        {
            "gpkg" or "geopackage" => FeatureExportFormat.GeoPackage,
            "shapefile" or "shp" => FeatureExportFormat.Shapefile,
            "xlsx" or "excel" => FeatureExportFormat.Excel,
            "fgdb" or "filegdb" or "filegeodatabase" or "gdb" => FeatureExportFormat.FileGeodatabase,
            "kml" => FeatureExportFormat.Kml,
            "csv" => FeatureExportFormat.Csv,
            "geojson" => FeatureExportFormat.GeoJson,
            "esrijson" or "featurecollection" => FeatureExportFormat.EsriJson,
            _ => null,
        };
    }

    /// <summary>
    /// A name for each layer that is legal in the format and not the same as any other's — the table, sheet or file it
    /// becomes.
    /// </summary>
    /// <param name="names">The layers' names, in the order they are written.</param>
    /// <param name="format">The format.</param>
    /// <returns>One name per layer, same order.</returns>
    /// <remarks>
    /// <para>
    /// <b>Two rules, and they are the same for the same input every time.</b> A name is made legal — a File Geodatabase's
    /// letters, digits and underscore, not starting with a digit; a workbook's thirty-one characters without
    /// <c>[ ] : * ? / \</c>; everything else a name safe as a file name — and then made unique <em>ignoring case</em>,
    /// because a GeoPackage table, an Excel sheet and a file on Windows all treat <c>Roads</c> and <c>roads</c> as one.
    /// A repeated name takes <c>_2</c>, <c>_3</c> … in order, cut short first when the format's length is the limit,
    /// so the suffix is what survives.
    /// </para>
    /// <para>
    /// <b>A Shapefile's ten-byte field names are not decided here</b> — they are GDAL's, and ADR-106 §5.6's own
    /// truncation rule, its <c>fieldnames.csv</c> and its <c>domains.csv</c> are not built in this step.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<string> LayerNames(IEnumerable<string> names, FeatureExportFormat format)
    {
        HashSet<string> taken = new(StringComparer.OrdinalIgnoreCase);
        List<string> chosen = [];

        foreach (string raw in names)
        {
            string legal = Legal(raw, format);
            string name = legal;

            for (int n = 2; !taken.Add(name); n++)
            {
                string tail = "_" + n.ToString(CultureInfo.InvariantCulture);
                int room = format == FeatureExportFormat.Excel ? WorkbookSheetName - tail.Length : int.MaxValue;

                name = (legal.Length > room ? legal[..room] : legal) + tail;
            }

            chosen.Add(name);
        }

        return chosen;
    }

    private static string Legal(string raw, FeatureExportFormat format)
    {
        StringBuilder name = new();

        foreach (char c in raw)
        {
            bool keep = format switch
            {
                FeatureExportFormat.FileGeodatabase => char.IsLetterOrDigit(c) || c == '_',
                FeatureExportFormat.Excel => c is not ('[' or ']' or ':' or '*' or '?' or '/' or '\\' or '\'')
                    && !char.IsControl(c),
                _ => c != ' ' && Array.IndexOf(System.IO.Path.GetInvalidFileNameChars(), c) < 0 && !char.IsControl(c),
            };

            name.Append(keep ? c : '_');
        }

        string text = name.ToString();

        if (format == FeatureExportFormat.Excel && text.Length > WorkbookSheetName)
        {
            text = text[..WorkbookSheetName];
        }

        if (format == FeatureExportFormat.FileGeodatabase && (text.Length == 0 || char.IsDigit(text[0])))
        {
            text = "L" + text;
        }

        return string.IsNullOrWhiteSpace(text) ? "layer" : text;
    }

    /// <summary>
    /// How many bytes an export is expected to take on disk: what a sample of its rows weighed as staging, scaled to
    /// the rows counted, doubled for staging plus output — ADR-106 §5.4.
    /// </summary>
    /// <param name="sampleBytes">What the sampled rows weighed as GeoJSON.</param>
    /// <param name="sampleRows">How many rows were sampled.</param>
    /// <param name="rows">The rows counted in the layer.</param>
    /// <returns>Bytes; an upper bound, and it says so.</returns>
    /// <remarks>
    /// <b>Errs high on purpose.</b> GeoJSON is among the widest of the formats — a GeoPackage, a workbook and a
    /// Shapefile of the same rows are smaller — and the rows sampled are the first, which are not known to be the
    /// heaviest. A layer with nothing sampled weighs nothing; the layer's fixed overhead is
    /// <see cref="PerLayerOverhead"/>.
    /// </remarks>
    internal static long EstimateBytes(long sampleBytes, long sampleRows, long rows)
    {
        long perRow = sampleRows <= 0 ? 0 : (sampleBytes + sampleRows - 1) / sampleRows;

        return checked((perRow * Math.Max(0, rows) * 2) + PerLayerOverhead);
    }

    /// <summary>What a layer costs beside its rows: a table's pages, a sheet's parts, a file's headers.</summary>
    internal const long PerLayerOverhead = 64 * 1024;
}
