using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Linq;
using MaxRev.Gdal.Core;
using OSGeo.OGR;
using OSGeo.OSR;
using Dataset = OSGeo.GDAL.Dataset;
using Gdal = OSGeo.GDAL.Gdal;
using GdalConst = OSGeo.GDAL.GdalConst;
using GdalVectorTranslateOptions = OSGeo.GDAL.GDALVectorTranslateOptions;

namespace Graticula.Import.Reader;

/// <summary>
/// Reads a File Geodatabase, in a process that is not the one serving requests.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-037] §5a.</b> That ADR first chose a Python worker to avoid writing a `.gdb` parser, while
/// accepting GDAL — which is the only thing that made writing one necessary. Reversed the same day:
/// .NET needs a binding rather than a parser, and a second language runtime buys nothing. Measured on
/// the owner's own geodatabases at 0.06 s against the Python worker's 0.29 s for the same layer.
/// </para>
/// <para>
/// <b>A separate process for the same reason `Graticula.Overlay.Worker` is one, and it is not the same
/// reason as before.</b> The overlay worker exists because an adversarial input cost 153 seconds and
/// 16.7 GB and could not be interrupted. This one exists because it parses a file somebody else chose:
/// [ADR-009] §2.2's words for keeping GDAL out of the serving process are that it *"removes an
/// untrusted-file parser from the process that serves public requests"*, and with the package in the
/// server image a child process is what keeps that true.
/// </para>
/// <para>
/// <b>The contract is one JSON request per line on stdin, one response per line on stdout</b> — the
/// same as the overlay worker's, so the host's pattern for spawning, bounding and killing needs no
/// second shape. Diagnostics go to stderr, which the host leaves attached so a GDAL message reaches
/// its log instead of vanishing. There is no cooperative deadline: the host owns the kill, because a
/// process that could be trusted to stop on request would not need to be separate.
/// </para>
/// <para>
/// <b>Nothing is unpacked.</b> `/vsizip/` reads inside the archive member by member, measured against
/// three real geodatabases — so a `.gdb.zip` needs one scratch file at its transferred size and no
/// expansion on our disk.
/// </para>
/// </remarks>
internal static class Program
{
    /// <summary>What every answer carries, so a reader never has to infer success.</summary>
    private static readonly JsonSerializerOptions Wire = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static int Main()
    {
        // <b>Once, before anything else.</b> `ConfigureAll` is what points the bindings at the native
        // payload NuGet unpacked; without it every driver is absent and the failure reads as *this
        // archive is not a geodatabase* rather than *GDAL is not loaded*.
        GdalBase.ConfigureAll();

        // GDAL writes to stderr through its own handler; routing it through ours keeps the two streams
        // honest — stdout stays the contract, stderr stays the diagnosis.
        Gdal.PushErrorHandler(new Gdal.GDALErrorHandlerDelegate(OnGdalMessage));

        string? line;

        while ((line = Console.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using JsonDocument request = JsonDocument.Parse(line);

                object? answered = Run(request.RootElement);

                // <b>`null` means the operation wrote its own lines.</b> `features` streams and ends with
                // its own trailer; answering again would put a second object after it and the host reads
                // until the trailer.
                if (answered is not null)
                {
                    Answer(answered);
                }
            }
            catch (Exception failed)
            {
                // <b>A failure is an answer, not an exit.</b> The host has a job row to write a reason
                // into, and a process that died silently would leave it saying only *failed* — which
                // `IJobStore` refuses precisely because nobody can act on it.
                Console.Error.WriteLine(failed);

                Answer(new { ok = false, error = $"{failed.GetType().Name}: {failed.Message}" });
            }
        }

        return 0;
    }

    /// <summary>Runs one request.</summary>
    private static object? Run(JsonElement request)
    {
        string operation = request.TryGetProperty("op", out JsonElement op)
            ? op.GetString() ?? string.Empty
            : string.Empty;

        return operation switch
        {
            // <b>What this build actually carries — [D-88](../../docs/architecture-debt.md).</b>
            // That row's second part asks for the driver set enumerated with its licences,
            // because GDAL's own `LICENSE.TXT` is a bill of materials — an MIT-style core
            // beside BSD, public-domain, **Apache-2.0 Esri** components, ISC, Info-ZIP and
            // Qhull — so *"GDAL is MIT"* must not be written as a finding. The licences are
            // read from that file; **which of them apply depends on which drivers were built
            // in**, and only the build can say. So it says.
            //
            // <b>Asked rather than assumed, because the payload is a package version.</b> The
            // native set arrives with `MaxRev.Gdal.*` and changes when that package does; a
            // list written into a document beside it would be right until the next upgrade.
            "drivers" => new
            {
                gdal = Gdal.VersionInfo("RELEASE_NAME"),
                vector = Enumerable.Range(0, Ogr.GetDriverCount())
                    .Select(i => Ogr.GetDriver(i).GetName())
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray(),
                raster = Enumerable.Range(0, Gdal.GetDriverCount())
                    .Select(i => Gdal.GetDriver(i).ShortName)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray(),
            },

            // <b>A liveness answer, because the host needs one before it trusts a new process.</b>
            // `GeometryWorkerPool` gives a worker a window to become responsive, and an import is a bad
            // first request to discover a missing native payload with.
            "ping" => new
            {
                ok = true,
                gdal = Gdal.VersionInfo("RELEASE_NAME"),
                drivers = new
                {
                    openFileGdb = Ogr.GetDriverByName("OpenFileGDB") is not null,
                    parquet = Ogr.GetDriverByName("Parquet") is not null,
                },

                // <b>Its own place in the scheduler's order, reported so the parent's setting is
                // observable.</b> [D-94](../../docs/architecture-debt.md): the host puts this process
                // below normal so an import loses to the requests the server is answering. A setting
                // nothing can see is a setting that quietly stops being applied, and this is the only
                // vantage point from which it is a fact rather than a line of code.
                priority = System.Diagnostics.Process.GetCurrentProcess().PriorityClass.ToString(),
            },

            "layers" => Layers(Text(request, "archive"), Encoding(request)),

            "convert" => Convert(
                Text(request, "archive"), Text(request, "layer"), Text(request, "out")),

            // <b>`features` writes many lines and returns nothing, which is why it is handled here
            // rather than through `Answer`.</b> ADR-038 §5: the features cross as newline-delimited
            // JSON on the pipe these two processes already have, because both ends are .NET now and
            // GeoParquet was chosen for a Python endpoint that no longer exists.
            "features" => Features(
                Text(request, "archive"), Text(request, "layer"), Encoding(request)),

            // <b>A geodatabase this repository can commit to a test rather than to git —
            // [D-95](../../docs/architecture-debt.md), [Q-138](../../docs/open-questions.md),
            // owner decision 2026-09-03.</b> Nothing automated read a *valid* archive: the only
            // test walked the pipeline with four empty files and asserted the refusal. The
            // corpus that would have tested the read path is the owner's client data, and a
            // public-domain File Geodatabase would put somebody else's bytes and a data licence
            // into a repository that is given away. So the fixture is built here, from geometry
            // this project already generates, by the same GDAL the reader uses.
            //
            // <b>The cost is stated rather than hidden, and it is the one Q-138 named.</b> This
            // tests that the reader reads what **GDAL's own writer** produces, not what Esri's
            // does — and most of the format risk lives in that difference. It is still a long
            // way from *no test reads a valid archive at all*: field types, geometry, encoding
            // and the multi-layer case are all exercised end to end, and a regression in the
            // reader is found by the suite rather than by a person.
            //
            // <b>In the reader because GDAL is in the reader.</b> The alternative was a test
            // project carrying its own hundred megabytes of native payload, or a second tool
            // with the same dependency — one more place for the version to drift from the one
            // that does the reading.
            "fixture" => Fixture(request),

            // <b>ADR-107: a layer taken away as a GeoPackage, a zipped shapefile's folder or a workbook.</b> The host
            // writes the rows as GeoJSON in the layer's own reference and names it; this translates, so GDAL stays
            // out of the serving process as ADR-009 §2.2 keeps it.
            //
            // <b>`append` (ADR-106's job, 2026-09-30) adds the layer to an output that already exists</b> instead of
            // making it, so several layers of one service can leave as one GeoPackage, File Geodatabase, KML or
            // workbook. Absent, the request is what ADR-107's route has always sent.
            // <b>ADR-112: a CSV or an Excel sheet with X and Y (or WKT) turned into GeoJSON</b>, which `layers` and
            // `features` then read as they read a shapefile. The coordinates are left as they are; the host says which
            // reference they are in.
            "tabular" => Tabular(
                Text(request, "in"), Text(request, "out"),
                request.TryGetProperty("x", out JsonElement xs) ? xs.GetString() : null,
                request.TryGetProperty("y", out JsonElement ys) ? ys.GetString() : null),

            "export" => Export(
                Text(request, "in"), Text(request, "out"), Text(request, "format"), Text(request, "layer"),
                request.TryGetProperty("append", out JsonElement append) && append.ValueKind == JsonValueKind.True),

            // ADR-157: imagery in a format the server does not read itself, written as the GeoTIFFs it does.
            "raster" => Raster(Text(request, "in"), Text(request, "out"),
                request.TryGetProperty("variable", out JsonElement variable) ? variable.GetString() : null,
                request.TryGetProperty("name", out JsonElement named) ? named.GetString() : null),

            _ => throw new ArgumentException(
                $"'{operation}' is not an operation. This reader answers 'ping', 'layers', "
                + "'convert', 'features', 'fixture', 'export', 'tabular' and 'raster'."),
        };
    }

    /// <summary>
    /// A raster in any format this build reads — JPEG 2000, NetCDF, HDF, ERDAS Imagine, ASCII grids — written as tiled,
    /// deflated GeoTIFFs in a directory: ADR-157. A file of subdatasets, as NetCDF and HDF are, gives its first variable
    /// with two dimensions of space, or the one named. A NetCDF whose bands are steps of time gives a GeoTIFF a step,
    /// each with the time it is — a mosaic the server dates, so a time slider plays it (ADR-153).
    /// </summary>
    private static object Raster(string source, string directory, string? variable, string? name)
    {
        using Dataset? opened = Gdal.Open(source, OSGeo.GDAL.Access.GA_ReadOnly);

        if (opened is null)
        {
            return new { ok = false, error = "GDAL cannot read it as a raster." };
        }

        Dataset dataset = opened;
        Dataset? sub = null;
        string? chosen = null;

        try
        {
            // NetCDF and HDF: subdatasets, a variable each.
            string[] subdatasets = opened.GetMetadata("SUBDATASETS") ?? [];
            List<string> variables = [.. subdatasets
                .Where(m => m.Contains("_NAME=", StringComparison.Ordinal))
                .Select(m => m[(m.IndexOf('=', StringComparison.Ordinal) + 1)..])];

            if (opened.RasterCount == 0 && variables.Count > 0)
            {
                string? pick = variable is { Length: > 0 }
                    ? variables.FirstOrDefault(v => v.EndsWith(":" + variable, StringComparison.Ordinal))
                    : variables[0];

                if (pick is null)
                {
                    return new
                    {
                        ok = false,
                        error = $"It has no variable '{variable}'. It has: {string.Join(", ", variables.Select(v => v.Split(':')[^1]))}.",
                    };
                }

                sub = Gdal.Open(pick, OSGeo.GDAL.Access.GA_ReadOnly);
                dataset = sub ?? throw new InvalidOperationException($"GDAL cannot open its variable {pick}.");
                chosen = pick.Split(':')[^1].Trim('"');
            }

            if (dataset.RasterCount == 0)
            {
                return new { ok = false, error = "It holds no raster bands." };
            }

            string projection = dataset.GetProjectionRef() ?? string.Empty;
            double[] transform = new double[6];
            dataset.GetGeoTransform(transform);

            if (projection.Length == 0 && transform[1] > 0 && Math.Abs(transform[0]) <= 360 && Math.Abs(transform[3]) <= 90)
            {
                // A grid of longitudes and latitudes that names no datum, as many NetCDF files are, is WGS 84.
                projection = "EPSG:4326";
            }

            if (projection.Length == 0)
            {
                return new { ok = false, error = "It carries no coordinate system, so this server cannot say where it is." };
            }

            // Steps of time: NetCDF names its extra dimension's values on each band.
            string[] bandTimes = [.. Enumerable.Range(1, dataset.RasterCount).Select(b =>
                (dataset.GetRasterBand(b).GetMetadata("") ?? []).FirstOrDefault(m => m.StartsWith("NETCDF_DIM_time=", StringComparison.Ordinal))
                    ?.Split('=', 2)[1] ?? string.Empty)];
            string units = (dataset.GetMetadata("") ?? []).FirstOrDefault(m => m.StartsWith("time#units=", StringComparison.Ordinal))?.Split('=', 2)[1] ?? string.Empty;
            bool timed = dataset.RasterCount > 1 && bandTimes.All(t => t.Length > 0) && TimeOrigin(units) is not null;

            string stem = (name is { Length: > 0 } ? Path.GetFileNameWithoutExtension(name) : Path.GetFileNameWithoutExtension(source))
                + (chosen is null ? string.Empty : " " + chosen);
            List<object> images = [];
            string[] common = ["-of", "GTiff", "-co", "TILED=YES", "-co", "COMPRESS=DEFLATE", "-co", "BIGTIFF=IF_SAFER", "-a_srs", projection];

            if (timed)
            {
                (DateTimeOffset origin, double perUnit) = TimeOrigin(units)!.Value;

                for (int band = 1; band <= dataset.RasterCount; band++)
                {
                    DateTimeOffset when = origin.AddSeconds(double.Parse(bandTimes[band - 1], CultureInfo.InvariantCulture) * perUnit);
                    string target = Path.Combine(directory, $"{Guid.NewGuid():N}.tif");
                    Translate(dataset, target, [.. common, "-b", band.ToString(CultureInfo.InvariantCulture)]);
                    images.Add(new { path = target, name = $"{stem} {when:yyyy-MM-dd}", acquired = when.ToString("O", CultureInfo.InvariantCulture) });
                }
            }
            else
            {
                string target = Path.Combine(directory, $"{Guid.NewGuid():N}.tif");
                Translate(dataset, target, common);
                images.Add(new { path = target, name = stem, acquired = (string?)null });
            }

            return new { ok = true, driver = opened.GetDriver().ShortName, variable = chosen, images };
        }
        finally
        {
            sub?.Dispose();
        }
    }

    private static void Translate(Dataset dataset, string target, string[] options)
    {
        using OSGeo.GDAL.GDALTranslateOptions translate = new(options);
        using Dataset? written = Gdal.wrapper_GDALTranslate(target, dataset, translate, null, null);

        if (written is null)
        {
            throw new InvalidOperationException($"GDAL could not write {Path.GetFileName(target)}: {Gdal.GetLastErrorMsg()}");
        }
    }

    /// <summary>A CF time's origin and seconds per unit, from <c>days since 1970-01-01</c> and the like.</summary>
    private static (DateTimeOffset Origin, double SecondsPerUnit)? TimeOrigin(string units)
    {
        string[] parts = units.Split(" since ", 2, StringSplitOptions.TrimEntries);

        if (parts.Length != 2)
        {
            return null;
        }

        double? per = parts[0].ToLowerInvariant() switch
        {
            "seconds" or "second" or "s" => 1,
            "minutes" or "minute" => 60,
            "hours" or "hour" or "h" => 3600,
            "days" or "day" or "d" => 86_400,
            _ => null,
        };

        return per is { } seconds && DateTimeOffset.TryParse(parts[1].Replace(' ', 'T').TrimEnd('Z') + (parts[1].Contains('+', StringComparison.Ordinal) ? string.Empty : "Z"),
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset origin)
            ? (origin, seconds)
            : null;
    }


    /// <summary>
    /// Writes a small File Geodatabase, for a test that needs a valid one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two layers, because one would not test the case that matters.</b> A geodatabase holds
    /// many, and [ADR-038](../../docs/adr/ADR-038-how-a-geodatabase-becomes-a-service.md) turns
    /// each into a layer of one service — so a fixture with a single layer would leave the
    /// multi-layer path unexercised, which is the path the owner's archives actually take.
    /// </para>
    /// <para>
    /// <b>A field of each kind the importer infers.</b> Text, integer, real and date, because
    /// what a reader gets wrong is types rather than coordinates, and a fixture with one string
    /// column would pass while the inference was broken.
    /// </para>
    /// <para>
    /// <b>Points and polygons, in EPSG:4326.</b> Two geometry kinds so the geometry-type
    /// declaration is exercised on both sides, and a reference the whole product understands so
    /// that a failure is about the archive rather than about a projection.
    /// </para>
    /// </remarks>
    /// <param name="request">Carries <c>path</c>, the directory to create.</param>
    /// <returns>What was written.</returns>
    private static object Fixture(JsonElement request)
    {
        string path = request.TryGetProperty("path", out JsonElement given)
            ? given.GetString() ?? string.Empty
            : string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("'fixture' needs a 'path' to write the geodatabase to.");
        }

        OSGeo.OGR.Driver driver = Ogr.GetDriverByName("OpenFileGDB")
            ?? throw new InvalidOperationException(
                "This build has no OpenFileGDB driver, so it cannot write a geodatabase. "
                + "GDAL has been able to since 3.6; ask 'ping' what this one carries.");

        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        int written = 0;

        using (DataSource made = driver.CreateDataSource(path, []))
        {
            SpatialReference wgs84 = new(null);
            wgs84.ImportFromEPSG(4326);

            written += Write(made, wgs84, "places", wkbGeometryType.wkbPoint);
            written += Write(made, wgs84, "parcels", wkbGeometryType.wkbPolygon);

            made.FlushCache();
        }

        Bound(path);

        return new { path, layers = 2, features = written };
    }

    /// <summary>
    /// Gives the fixture's <c>count</c> field on <c>places</c> a range domain — ADR-065.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A range and not a list, because a range is what this binding can write.</b> GDAL's C#
    /// binding creates a range domain and cannot create a coded-value one, and the OGR data source that
    /// wrote the layers cannot add a domain at all — so the archive is reopened as a GDAL dataset, the
    /// domain added there, and the field altered to name it. What the import then reads is the
    /// <c>GPRangeDomain2</c> item GDAL's writer puts in <c>GDB_Items</c>, which is the path an
    /// Esri-written archive's domains take too. A coded-value list read from an Esri-written archive was
    /// measured by hand against GDAL's own <c>Domains.gdb</c> test archive, and is recorded in ADR-065
    /// rather than committed, for Q-138's reason.
    /// </para>
    /// <para>
    /// <b>0 to 100, and every value the fixture writes is inside it</b> (3 to 15), so the import is
    /// tested carrying a domain rather than tripping over one.
    /// </para>
    /// </remarks>
    private static void Bound(string path)
    {
        // `ALTER_DOMAIN_FLAG`, which the binding does not name: GDAL's ogr_core.h gives it as 64.
        const int AlterDomain = 64;

        using Dataset opened = Gdal.OpenEx(path, (uint)(GdalConst.OF_VECTOR | GdalConst.OF_UPDATE), null, null, null)
            ?? throw new InvalidOperationException($"GDAL could not reopen the fixture it just wrote at '{path}'.");

        using FieldDomain visits = Ogr.CreateRangeFieldDomain(
            "Visits", "How many visits a place has had", FieldType.OFTInteger, FieldSubType.OFSTNone,
            0, true, 100, true);

        if (!opened.AddFieldDomain(visits))
        {
            throw new InvalidOperationException("GDAL refused to add the fixture's range domain.");
        }

        using Layer places = opened.GetLayerByName("places");
        using FeatureDefn definition = places.GetLayerDefn();

        using FieldDefn bounded = new("count", FieldType.OFTInteger);
        bounded.SetDomainName("Visits");

        if (places.AlterFieldDefn(definition.GetFieldIndex("count"), bounded, AlterDomain) != 0)
        {
            throw new InvalidOperationException("GDAL refused to give the fixture's count field its domain.");
        }

        opened.FlushCache();
    }

    /// <summary>One layer of the fixture, with its fields and a handful of features.</summary>
    /// <param name="into">The geodatabase.</param>
    /// <param name="reference">EPSG:4326.</param>
    /// <param name="name">The layer's name.</param>
    /// <param name="kind">Point or polygon.</param>
    /// <returns>How many features were written.</returns>
    private static int Write(
        DataSource into, SpatialReference reference, string name, wkbGeometryType kind)
    {
        Layer layer = into.CreateLayer(name, reference, kind, []);

        foreach ((string field, FieldType type) in new[]
        {
            ("name", FieldType.OFTString),
            ("count", FieldType.OFTInteger),
            ("area", FieldType.OFTReal),
            ("seen", FieldType.OFTDate),
        })
        {
            using FieldDefn defined = new(field, type);

            if (type == FieldType.OFTString)
            {
                defined.SetWidth(64);
            }

            // <b>One field with an alias and three without</b>, so the import is tested carrying a
            // label and not inventing one — ADR-063. A geodatabase's alias is its owner's label
            // for the field, and the published layer should show it.
            if (field == "count")
            {
                defined.SetAlternativeName("Visit count");
            }

            layer.CreateField(defined, 1);
        }

        int written = 0;

        for (int i = 1; i <= 5; i++)
        {
            using Feature feature = new(layer.GetLayerDefn());

            feature.SetField("name", $"{name} {i}");
            feature.SetField("count", i * 3);
            feature.SetField("area", i * 1.5);
            feature.SetField("seen", 2026, 9, 3, 12, 0, 0, 0);

            double x = 32.0 + (i * 0.01);
            double y = 39.0 + (i * 0.01);

            using Geometry geometry = kind == wkbGeometryType.wkbPoint
                ? Point(x, y)
                : Square(x, y);

            feature.SetGeometry(geometry);
            layer.CreateFeature(feature);
            written++;
        }

        layer.SyncToDisk();

        return written;
    }

    private static Geometry Point(double x, double y)
    {
        Geometry point = new(wkbGeometryType.wkbPoint);
        point.AddPoint_2D(x, y);
        return point;
    }

    private static Geometry Square(double x, double y)
    {
        Geometry ring = new(wkbGeometryType.wkbLinearRing);

        ring.AddPoint_2D(x, y);
        ring.AddPoint_2D(x + 0.005, y);
        ring.AddPoint_2D(x + 0.005, y + 0.005);
        ring.AddPoint_2D(x, y + 0.005);
        ring.AddPoint_2D(x, y);

        Geometry polygon = new(wkbGeometryType.wkbPolygon);
        polygon.AddGeometry(ring);

        return polygon;
    }

    /// <summary>
    /// One geometry as base64 WKB, or null when the feature carries none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Little-endian explicitly.</b> The reader on the other side takes the byte order from the
    /// first byte and would accept either, but a format written by whichever end happens to run is a
    /// format nobody can capture and replay.
    /// </para>
    /// <para>
    /// <b>25D geometries keep their Z here and lose it there.</b> GDAL writes the 2.5D form — the type
    /// code with the high bit set — and <c>WkbReader</c> reads that, drops the Z and says it did. This
    /// end does not flatten, because a reader that discarded ordinates before anybody asked would make
    /// *what was lost* unanswerable at the only place that can report it.
    /// </para>
    /// </remarks>
    private static string? Wkb(Geometry? geometry)
    {
        if (geometry is null)
        {
            return null;
        }

        byte[] bytes = new byte[geometry.WkbSize()];

        // A non-zero return is OGR's failure code, and an empty array would then be read as a
        // geometry rather than as a refusal.
        if (geometry.ExportToWkb(bytes, wkbByteOrder.wkbNDR) != 0)
        {
            throw new InvalidOperationException(
                "GDAL declined to write this geometry as WKB, which means it is something OGR holds "
                + "and cannot serialise rather than anything about the layer.");
        }

        return System.Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// Every feature of one layer, a line at a time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Three shapes of line, and the last one is what makes the stream self-terminating.</b> A header
    /// naming the coordinate system and the fields, then one line per feature, then a trailer with the
    /// count. The host reads until the trailer rather than until the pipe closes, so a reader that dies
    /// halfway is a stream that ended without a trailer — which is a different thing from a layer with no
    /// features, and the two must not look alike.
    /// </para>
    /// <para>
    /// <b>The geometry is WKB, base64 in the JSON line, and it was GeoJSON for one afternoon.</b>
    /// ADR-038 §4B chose GeoJSON because GDAL writes it in one call and this server already reads it —
    /// and the first real archive refused all eight of its layers, because
    /// <c>GeoJsonGeometry.TryRead</c> enforces RFC 7946: coordinates are WGS 84 longitude and latitude.
    /// The owner's data is EPSG:2952, so every position was *outside WGS 84* and the check was right.
    /// GeoJSON's coordinate system is part of the format; a wire carrying a projected layer needs a
    /// format with no opinion about it, and WKB is that. <c>WkbReader</c> reads it straight into the
    /// server's geometry model, reports Z it had to drop, and refuses curves — all of which the GeoJSON
    /// path did silently or not at all.
    /// </para>
    /// <para>
    /// <b>Coordinates are not reprojected here.</b> The header says which system they are in and the
    /// importer decides what to store them as, which is where that decision already lives — a reader that
    /// silently reprojected would be making a storage decision from inside a parser.
    /// </para>
    /// </remarks>
    private static object? Features(string archive, string layerName, string? encoding)
    {
        List<string> said = [];

        _messages = said;

        using Dataset source = Open(archive, encoding);
        using Layer layer = source.GetLayerByName(layerName)
            ?? throw new ArgumentException(
                $"'{layerName}' is not a layer in this archive. Ask 'layers' for the ones that are.");

        using FeatureDefn definition = layer.GetLayerDefn();

        // <b>ADR-065: the geodatabase's domains and this table's subtypes</b>, which GDAL's binding
        // cannot hand over and the catalogue's own XML can. Read once per layer, and an archive whose
        // catalogue cannot be read imports as it did before — `catalogUnread` says why.
        GeodatabaseCatalog catalog = GeodatabaseCatalog.Read(Vsi(archive), DriverOf(source));

        List<object> fields = [];
        Dictionary<string, string?> ownDomains = new(StringComparer.OrdinalIgnoreCase);

        for (int f = 0; f < definition.GetFieldCount(); f++)
        {
            using FieldDefn field = definition.GetFieldDefn(f);

            string? domainName = Nothing(field.GetDomainName());
            ownDomains[field.GetName()] = domainName;

            fields.Add(new
            {
                name = field.GetName(),
                type = field.GetFieldTypeName(field.GetFieldType()),

                // <b>The label the archive's owner gave the field, on the stream that imports it and
                // not only on the inspection that describes it</b> — ADR-063. It was reported and
                // dropped, because the schema had nowhere to put it; it has now.
                alias = Nothing(field.GetAlternativeName()),

                // <b>The field's domain, whole — ADR-065.</b> The inspection reports its name; this is
                // the list or the range, in the ArcGIS domain object's shape the server stores.
                domain = catalog.Domain(domainName),
            });
        }

        Answer(new
        {
            ok = true,
            header = true,
            srid = Epsg(layer),
            geometry = definition.GetGeomType().ToString(),
            fields,

            // The table's subtypes, with the archive's own field names; the importer maps them.
            subtypes = catalog.Subtypes(layerName, ownDomains),
            catalogUnread = catalog.Unread,
        });

        long written = 0;

        layer.ResetReading();

        for (Feature feature = layer.GetNextFeature();
             feature is not null;
             feature = layer.GetNextFeature())
        {
            using (feature)
            {
                Dictionary<string, object?> values = new(StringComparer.Ordinal);

                for (int f = 0; f < definition.GetFieldCount(); f++)
                {
                    using FieldDefn field = definition.GetFieldDefn(f);

                    string name = field.GetName();

                    if (!feature.IsFieldSet(f) || feature.IsFieldNull(f))
                    {
                        values[name] = null;
                        continue;
                    }

                    // <b>Read as the type the field declares, not as text.</b> A double written as a
                    // string arrives as a text column on the other side, and the importer would size a
                    // varchar for a number — which is how a schema comes to disagree with its own data.
                    values[name] = field.GetFieldType() switch
                    {
                        FieldType.OFTInteger => feature.GetFieldAsInteger(f),
                        FieldType.OFTInteger64 => feature.GetFieldAsInteger64(f),
                        FieldType.OFTReal => feature.GetFieldAsDouble(f),
                        _ => feature.GetFieldAsString(f),
                    };
                }

                // <b>Not in a `using`, because this does not own what it points at.</b> GDAL
                // documents `OGR_F_GetGeometryRef` as returning the feature's own geometry — the caller
                // must not free it — and the feature is disposed one line later. A `using` here would
                // read as ownership that is not ours to claim.
                Geometry? geometry = feature.GetGeometryRef();

                Answer(new { g = Wkb(geometry), v = values });

                written++;
            }
        }

        Answer(new { ok = true, done = true, features = written, messages = said });

        // Nothing for the caller to print: every line is already out.
        return null;
    }

    /// <summary>
    /// What is in the archive, without reading any of it.
    /// </summary>
    /// <remarks>
    /// <b>Every layer the driver reports, including the ones nobody would publish.</b> A geodatabase's
    /// attachment tables have no geometry — one of the owner's archives holds six of them beside six
    /// feature classes — and filtering them out here would be deciding for the screen. The screen needs
    /// to say *why* something is not offered rather than quietly shortening its list, so the geometry
    /// type is reported and the caller chooses.
    /// </remarks>
    private static object Layers(string archive, string? encoding)
    {
        using Dataset source = Open(archive, encoding);

        List<object> layers = [];

        for (int i = 0; i < source.GetLayerCount(); i++)
        {
            using Layer layer = source.GetLayer(i);
            using FeatureDefn definition = layer.GetLayerDefn();

            List<object> fields = [];

            for (int f = 0; f < definition.GetFieldCount(); f++)
            {
                using FieldDefn field = definition.GetFieldDefn(f);

                fields.Add(new
                {
                    name = field.GetName(),
                    type = field.GetFieldTypeName(field.GetFieldType()),

                    // The geodatabase's own alias, which is what an operator reads in ArcGIS. Reported
                    // here for the picker, and carried by the `features` stream to the published
                    // layer as its label since ADR-063 gave the schema somewhere to put it.
                    alias = Nothing(field.GetAlternativeName()),

                    // A coded value domain, by name. We have no domains either; this is the same
                    // honesty for the same reason.
                    domain = Nothing(field.GetDomainName()),
                });
            }

            layers.Add(new
            {
                name = layer.GetName(),

                // `wkbNone` for a table. That is the fact a picker needs to tell an attachment table
                // apart from a feature class.
                geometry = ((wkbGeometryType)definition.GetGeomType()).ToString(),

                // Force, because a lazy count is a guess and a picker showing one is worse than a
                // picker that waited. These archives answer in milliseconds.
                features = layer.GetFeatureCount(1),

                srid = Epsg(layer),

                fields,
            });
        }

        return new { ok = true, layers };
    }

    /// <summary>
    /// Writes one layer to GeoParquet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>GeoParquet because [Q-74] chose it, and the choice carries two things GeoJSON does not.</b>
    /// The column types survive — our GeoJSON path infers them by scanning every feature, and a
    /// geodatabase already knows them — and the coordinate reference travels inside the file as
    /// PROJJSON with its authority code. Nothing has to ask the operator for an `srid`.
    /// </para>
    /// <para>
    /// <b>`VectorTranslate` rather than a feature loop.</b> It is `ogr2ogr` as a library call: the same
    /// code path, without the process, and without us reimplementing type mapping that GDAL has
    /// already argued about for twenty years.
    /// </para>
    /// <para>
    /// <b>Z is dropped by the Parquet writer and it says so.</b> GDAL emits *"attempt to write Z
    /// geometries to layer … Z component will be discarded"*, and most of the owner's real layers are
    /// 3D. [ADR-024] condition 5's rule is that a loss is reported at the moment of the loss, so the
    /// messages are captured and returned rather than left on stderr for a log nobody reads while the
    /// operator is told the import worked.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Translates a GeoJSON file into a GeoPackage, a shapefile folder, a workbook, a File Geodatabase folder, KML, CSV
    /// or GeoJSON — ADR-107, and ADR-106's formats on the same road.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The input names its reference in the legacy <c>crs</c> member. <b>GeoPackage, shapefile, File Geodatabase and the
    /// workbook are not reprojected</b>: the rows leave in the reference they are stored in, so a TUREF layer leaves as
    /// TUREF. A shapefile and a File Geodatabase are written into <paramref name="output"/> as a folder, which the host
    /// zips; the shapefile's text is UTF-8 and says so in a <c>.cpg</c>. A workbook holds the attributes only.
    /// </para>
    /// <para>
    /// <b>KML, GeoJSON and CSV are WGS 84</b>, because their readers assume it: KML's specification fixes it, RFC 7946
    /// fixes it for GeoJSON, and a spreadsheet's longitude and latitude are what a CSV of places is opened for (ADR-106
    /// §5.6). A point layer's CSV carries X and Y columns; any other geometry is one WKT column. The CSV starts with a
    /// byte-order mark so Excel reads its Turkish as UTF-8, as the console's own CSV always did.
    /// </para>
    /// </remarks>
    /// <param name="input">The staged GeoJSON.</param>
    /// <param name="output">The file or folder to write.</param>
    /// <param name="format">The format's request token.</param>
    /// <param name="layer">The layer's name in the output.</param>
    /// <param name="append">
    /// <b>Add to an output that exists</b> — GDAL's <c>-update -append</c>. The host asks for it only for the formats it
    /// has measured able to (ADR-106 §5.6); a format that could not would write a second dataset over the first.
    /// </param>
    private static object Export(string input, string output, string format, string layer, bool append = false)
    {
        string driver = format switch
        {
            "gpkg" => "GPKG",
            "shapefile" => "ESRI Shapefile",
            "xlsx" => "XLSX",
            "fgdb" => "OpenFileGDB",
            "kml" => "LIBKML",
            "csv" => "CSV",
            "geojson" => "GeoJSON",
            _ => throw new ArgumentException(
                $"'{format}' is not an export format; 'gpkg', 'shapefile', 'xlsx', 'fgdb', 'kml', 'csv' and 'geojson' are."),
        };

        List<string> said = [];
        _messages = said;

        try
        {
            using Dataset source = Gdal.OpenEx(input, 0, null, null, null)
                ?? throw new InvalidOperationException($"GDAL could not open '{input}'.");

            List<string> options = ["-f", driver, "-nln", layer];

            if (append) options.AddRange(["-update", "-append"]);

            if (format == "shapefile") options.AddRange(["-lco", "ENCODING=UTF-8"]);

            if (format is "kml" or "csv" or "geojson") options.AddRange(["-t_srs", "EPSG:4326"]);

            if (format == "geojson") options.AddRange(["-lco", "RFC7946=YES"]);

            if (format == "csv")
            {
                bool points = source.GetLayerCount() > 0
                    && Ogr.GT_Flatten(source.GetLayer(0).GetGeomType()) == wkbGeometryType.wkbPoint;

                options.AddRange(["-lco", points ? "GEOMETRY=AS_XY" : "GEOMETRY=AS_WKT", "-lco", "WRITE_BOM=YES"]);
            }

            using Dataset written = Gdal.wrapper_GDALVectorTranslateDestName(
                output, source, new GdalVectorTranslateOptions([.. options]), null, null)
                ?? throw new InvalidOperationException($"GDAL refused to write '{layer}' as {driver}. {string.Join(" ", said)}");
        }
        finally
        {
            _messages = null;
        }

        return new { ok = true, messages = said };
    }

    /// <summary>
    /// A CSV or the first sheet of an Excel workbook as GeoJSON, its points from X and Y columns or its shapes from a
    /// WKT column — ADR-112.
    /// </summary>
    /// <remarks>
    /// <b>GDAL's CSV driver does the reading</b>, with the columns it should look for named: the ones the caller gives,
    /// then the usual English and Turkish names. A workbook is written to CSV first, because the Excel driver reads
    /// attributes and has no X/Y option. Numbers stay numbers (<c>AUTODETECT_TYPE</c>).
    /// </remarks>
    private static object Tabular(string input, string output, string? x, string? y)
    {
        List<string> said = [];
        _messages = said;

        string? scratch = null;

        try
        {
            string csv = input;

            if (input.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                scratch = Path.Combine(Path.GetDirectoryName(output) ?? Path.GetTempPath(), Path.GetFileNameWithoutExtension(input) + ".sheet.csv");

                using Dataset book = Gdal.OpenEx(input, 0, null, null, null)
                    ?? throw new InvalidOperationException("GDAL could not open the workbook.");
                using Dataset sheet = Gdal.wrapper_GDALVectorTranslateDestName(
                    scratch, book, new GdalVectorTranslateOptions(["-f", "CSV", "-lco", "GEOMETRY=AS_WKT"]), null, null)
                    ?? throw new InvalidOperationException($"The workbook could not be read as a table. {string.Join(" ", said)}");

                csv = scratch;
            }

            string[] xNames = [.. new[] { x }.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!)
                .Concat(["x", "lon", "lng", "long", "longitude", "boylam", "easting", "doğu"])];
            string xs = string.Join(",", xNames);

            // <b>Which X column was used, said back</b>, so the host can tell degrees from metres: an `easting` read as
            // longitude lands a layer in the sea off Africa, and the host refuses that without an EPSG code.
            string? matched = null;

            using (Dataset plain = Gdal.OpenEx(csv, 0, ["CSV"], null, null)
                ?? throw new InvalidOperationException("GDAL could not open the table."))
            {
                Layer? head = plain.GetLayerCount() > 0 ? plain.GetLayer(0) : null;
                FeatureDefn? defn = head?.GetLayerDefn();
                HashSet<string> present = new(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; defn is not null && i < defn.GetFieldCount(); i++)
                {
                    present.Add(defn.GetFieldDefn(i).GetName());
                }

                matched = xNames.FirstOrDefault(present.Contains);
            }
            string ys = string.Join(",", new[] { y }.Where(n => !string.IsNullOrWhiteSpace(n))
                .Concat(["y", "lat", "latitude", "enlem", "northing", "kuzey"]));

            using Dataset table = Gdal.OpenEx(csv, 0, ["CSV"],
                [$"X_POSSIBLE_NAMES={xs}", $"Y_POSSIBLE_NAMES={ys}", "GEOM_POSSIBLE_NAMES=wkt,WKT,geometry,geom,the_geom,shape",
                 "KEEP_GEOM_COLUMNS=NO", "AUTODETECT_TYPE=YES"], null)
                ?? throw new InvalidOperationException("GDAL could not open the table.");

            {
                Layer? first = table.GetLayerCount() > 0 ? table.GetLayer(0) : null;

                if (first is null || first.GetGeomType() == wkbGeometryType.wkbNone)
                {
                    return new
                    {
                        ok = false,
                        error = "No coordinates were found in the table: name its columns x and y (or lon and lat), or send "
                            + "a WKT column, or say which columns hold them.",
                    };
                }
            }

            using Dataset written = Gdal.wrapper_GDALVectorTranslateDestName(
                output, table, new GdalVectorTranslateOptions(["-f", "GeoJSON"]), null, null)
                ?? throw new InvalidOperationException($"The table could not be written as GeoJSON. {string.Join(" ", said)}");

            return new { ok = true, x = matched, messages = said };
        }
        finally
        {
            _messages = null;

            if (scratch is not null && File.Exists(scratch))
            {
                File.Delete(scratch);
            }
        }
    }

    private static object Convert(string archive, string layer, string output)
    {
        if (File.Exists(output))
        {
            File.Delete(output);
        }

        List<string> said = [];

        _messages = said;

        try
        {
            Stopwatch clock = Stopwatch.StartNew();

            using (Dataset input = Gdal.OpenEx(Vsi(archive), 0, null, null, null))
            {
                if (input is null)
                {
                    throw new InvalidOperationException($"GDAL could not open '{archive}'.");
                }

                using Dataset written = Gdal.wrapper_GDALVectorTranslateDestName(
                    output,
                    input,
                    new GdalVectorTranslateOptions(
                        ["-f", "Parquet", "-lco", "GEOMETRY_ENCODING=WKB", layer]),
                    null,
                    null);

                if (written is null)
                {
                    throw new InvalidOperationException(
                        $"GDAL refused to convert '{layer}'. {string.Join(" ", said)}");
                }
            }

            clock.Stop();

            // <b>Measured after the dataset is disposed, because it buffers until it closes.</b> The
            // first version of this measurement read the file inside the `using` block and reported
            // 0 KB for a conversion that had worked.
            long bytes = new FileInfo(output).Length;

            return new
            {
                ok = true,
                layer,
                @out = output,
                bytes,
                milliseconds = (long)clock.Elapsed.TotalMilliseconds,
                warnings = said,
            };
        }
        catch
        {
            // <b>GDAL creates the output before it fails, so a refusal leaves a file behind.</b>
            // Measured: a layer name that does not exist is refused with GDAL's own message *and*
            // leaves a 0-byte Parquet in the scratch directory. ADR-037 condition 6 says the scratch
            // file goes whether the job succeeds or fails, and this is the half of that which belongs
            // to the reader rather than to the host.
            if (File.Exists(output))
            {
                try
                {
                    File.Delete(output);
                }
                catch (IOException swept)
                {
                    // A file we cannot remove is worth saying so about and not worth failing twice for:
                    // the original refusal is the one the caller needs.
                    Console.Error.WriteLine($"could not remove {output}: {swept.Message}");
                }
            }

            throw;
        }
        finally
        {
            _messages = null;
        }
    }

    // ------------------------------------------------------------------------------ plumbing

    /// <summary>Where GDAL's own messages go while a conversion is running.</summary>
    /// <remarks>
    /// <b>A field rather than a parameter because the handler is a C callback.</b> GDAL's error handler
    /// is process-wide and takes no state, so the only way to attribute a message to the operation that
    /// caused it is to hold the list while that operation runs.
    /// </remarks>
    private static List<string>? _messages;

    private static void OnGdalMessage(int kind, int code, IntPtr text)
    {
        string? message = System.Runtime.InteropServices.Marshal.PtrToStringAnsi(text);

        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        _messages?.Add(message);

        Console.Error.WriteLine(
            string.Create(CultureInfo.InvariantCulture, $"gdal[{kind}/{code}] {message}"));
    }

    /// <summary>
    /// The character set an operator says a shapefile's attribute table is in, or null.
    /// </summary>
    /// <remarks>
    /// <b>Optional and unused for a geodatabase, which stores its own encoding.</b> A
    /// shapefile's DBF does not have to: a `.cpg` file beside it declares one and often is
    /// not there. Measured on this repository's own corpus — GDAL reads
    /// `turkish_cp1254.zip` correctly because it has a `.cpg`, and reads
    /// `turkish_undeclared.zip` as mojibake because it does not. **Our own parser gets that
    /// second case wrong too**, and its own tests assert as much, which is why
    /// [ADR-024](../../docs/adr/ADR-024-shapefile-import.md) makes it the operator's to
    /// say. This is the channel that answer travels down.
    /// </remarks>
    private static string? Encoding(JsonElement request) =>
        request.TryGetProperty("encoding", out JsonElement said) ? Nothing(said.GetString()) : null;

    /// <summary>Opens an archive, or says which one it could not open.</summary>
    /// <remarks>
    /// <b>The encoding is a GDAL config option rather than an open option, which is not a
    /// preference.</b> `SHAPE_ENCODING` is what the OGR Shapefile driver reads, and it is
    /// read when the DBF is opened — so it has to be set before this call and cleared
    /// after, or one request's answer changes the next request's. That is a real hazard
    /// here: this process is long-lived and serves many requests.
    /// </remarks>
    private static Dataset Open(string archive, string? encoding = null)
    {
        /*
          <b>`OpenEx` with an open option, and the two channels that do not work were
          measured rather than assumed.</b> The Shapefile driver takes the DBF's character
          set three ways and only one of them is per-request:

          - `Gdal.SetConfigOption("SHAPE_ENCODING", …)` before the open: **no effect.**
            Read `turkish_undeclared.zip` — CP1254 bytes, no `.cpg` — and it came back as
            `\uFFFDi\uFFFDli \uFFFDay\uFFFDr\uFFFD` with the option set and without it.
          - `SHAPE_ENCODING` in the process environment at start: **works**, and is useless
            here. This process is long-lived and shared, so an encoding fixed at start is
            one encoding for every import. Setting the variable from inside does not work
            either — CPL reads the environment before any of this runs.
          - `-oo ENCODING=` as an open option: per open, which is the only shape that
            matches a per-request protocol.

          <b>So the type changes from `OGR.DataSource` to `GDAL.Dataset`</b>, because
          `Ogr.Open` has no open options and `Gdal.OpenEx` does. The layers are the same
          `OGR.Layer` objects either way, which is why the rest of this file is unchanged.
        */
        string[]? options = encoding is null ? null : ["ENCODING=" + encoding];

        return Gdal.OpenEx(Vsi(archive), (uint)GdalConst.OF_VECTOR, null, options, null)
            ?? throw new InvalidOperationException(
                $"GDAL could not open '{archive}'. A File Geodatabase is a directory, so it "
                + "arrives zipped and is read through /vsizip/ — an archive that is not one, "
                + "or one holding nothing GDAL recognises, fails here.");
    }

    /// <summary>
    /// Turns a path into something GDAL reads without unpacking it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A path that is already a <c>/vsi*</c> handle passes through, which is what lets a caller point at
    /// object storage later without this method learning about it.
    /// </para>
    /// <para>
    /// <b>And a zip is descended into, because the archive root is not the dataset.</b> This first
    /// version stopped at <c>/vsizip/x.zip</c>, which is the folder holding the geodatabase rather than
    /// the geodatabase — <c>OpenFileGDB</c> opens a directory named <c>something.gdb</c> and does not
    /// go looking for one. Every upload failed with *GDAL could not open*, and the earlier measurement
    /// that said this worked had been pointed at an **already-extracted** <c>.gdb</c> directory sitting
    /// beside the archive. Two different things named by two paths that differ by four characters,
    /// which is how a measurement comes to prove something adjacent to the claim.
    /// </para>
    /// </remarks>
    private static string Vsi(string archive)
    {
        if (archive.StartsWith("/vsi", StringComparison.Ordinal))
        {
            return archive;
        }

        if (!archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return archive;
        }

        string inside = "/vsizip/" + archive.Replace('\\', '/');

        return Descend(inside) ?? inside;
    }

    /// <summary>
    /// The geodatabase inside an archive, as a path GDAL can open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Read from the archive's own index, not guessed from its file name.</b>
    /// <c>PointofInvestigation.gdb.zip</c> usually holds <c>PointofInvestigation.gdb/</c> and sometimes
    /// does not — an archive made by selecting the folder in Explorer, or one renamed after the fact,
    /// carries whatever it carries. <c>ReadDirRecursive</c> asks.
    /// </para>
    /// <para>
    /// <b>The shortest match, so a nested backup does not win.</b> A geodatabase can contain another
    /// directory ending in <c>.gdb</c>; the one nearest the archive root is the one somebody meant to
    /// send. Null when there is none, and the caller then opens the root — which is right for the zipped
    /// shapefile this same door will take later.
    /// </para>
    /// </remarks>
    private static string? Descend(string inside)
    {
        string[]? entries = Gdal.ReadDirRecursive(inside);

        if (entries is null)
        {
            return null;
        }

        string? best = null;

        foreach (string entry in entries)
        {
            string path = entry.Replace('\\', '/').TrimEnd('/');

            // The entry may be a file *inside* the geodatabase — `x.gdb/a00000001.gdbtable` — so the
            // directory is the prefix up to and including the `.gdb` segment rather than the entry.
            int at = path.IndexOf(".gdb/", StringComparison.OrdinalIgnoreCase);

            string? candidate = at >= 0
                ? path[..(at + 4)]
                : path.EndsWith(".gdb", StringComparison.OrdinalIgnoreCase) ? path : null;

            if (candidate is not null && (best is null || candidate.Length < best.Length))
            {
                best = candidate;
            }
        }

        if (best is not null)
        {
            return inside + "/" + best;
        }

        // <b>ADR-120: a GeoPackage or a KML in the archive</b>, the one nearest its root — GDAL reads either through
        // /vsizip/ in place, so neither is unpacked. A shapefile's archive has neither and opens at its root as before.
        string? file = entries
            .Select(entry => entry.Replace('\\', '/'))
            .Where(path => path.EndsWith(".gpkg", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".kml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path.Count(c => c == '/'))
            .ThenBy(path => path.Length)
            .FirstOrDefault();

        return file is null ? null : inside + "/" + file;
    }

    /// <summary>The layer's authority code, or null when it has none.</summary>
    /// <remarks>
    /// <b>Resolved through PROJ rather than matched as a string.</b> Our shapefile import demands an
    /// `srid` from the operator because a `.prj` is bare WKT and comparing it to a code by text is how a
    /// layer comes to declare a system it is not in. This asks the authority database instead, so there
    /// is nothing to ask the operator.
    /// </remarks>
    private static int? Epsg(Layer layer)
    {
        using SpatialReference? reference = layer.GetSpatialRef();

        if (reference is null)
        {
            return null;
        }

        reference.AutoIdentifyEPSG();

        string? code = reference.GetAuthorityCode(null);

        return int.TryParse(code, CultureInfo.InvariantCulture, out int parsed) ? parsed : null;
    }

    private static string Text(JsonElement request, string name) =>
        request.TryGetProperty(name, out JsonElement value) && value.GetString() is { } said
            ? said
            : throw new ArgumentException($"The request has no '{name}'.");

    /// <summary>The short name of the driver that opened an archive, or empty.</summary>
    private static string DriverOf(Dataset source)
    {
        using OSGeo.GDAL.Driver? driver = source.GetDriver();
        return driver?.ShortName ?? string.Empty;
    }

    private static string? Nothing(string? said) =>
        string.IsNullOrWhiteSpace(said) ? null : said;

    /// <summary>Writes one response and flushes it.</summary>
    /// <remarks>
    /// <b>The flush is load-bearing.</b> A pipe buffers, and a host waiting on a line that is sitting in
    /// this process's buffer looks exactly like a process that hung — which the host would then kill,
    /// correctly and for the wrong reason.
    /// </remarks>
    private static void Answer(object answer)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(answer, Wire));
        Console.Out.Flush();
    }
}
