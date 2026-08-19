using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MaxRev.Gdal.Core;
using OSGeo.OGR;
using OSGeo.OSR;
using Dataset = OSGeo.GDAL.Dataset;
using Gdal = OSGeo.GDAL.Gdal;
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
            },

            "layers" => Layers(Text(request, "archive")),

            "convert" => Convert(
                Text(request, "archive"), Text(request, "layer"), Text(request, "out")),

            // <b>`features` writes many lines and returns nothing, which is why it is handled here
            // rather than through `Answer`.</b> ADR-038 §5: the features cross as newline-delimited
            // JSON on the pipe these two processes already have, because both ends are .NET now and
            // GeoParquet was chosen for a Python endpoint that no longer exists.
            "features" => Features(Text(request, "archive"), Text(request, "layer")),

            _ => throw new ArgumentException(
                $"'{operation}' is not an operation. This reader answers 'ping', 'layers', 'convert' "
                + "and 'features'."),
        };
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
    private static object? Features(string archive, string layerName)
    {
        List<string> said = [];

        _messages = said;

        using DataSource source = Open(archive);
        using Layer layer = source.GetLayerByName(layerName)
            ?? throw new ArgumentException(
                $"'{layerName}' is not a layer in this archive. Ask 'layers' for the ones that are.");

        using FeatureDefn definition = layer.GetLayerDefn();

        List<object> fields = [];

        for (int f = 0; f < definition.GetFieldCount(); f++)
        {
            using FieldDefn field = definition.GetFieldDefn(f);

            fields.Add(new { name = field.GetName(), type = field.GetFieldTypeName(field.GetFieldType()) });
        }

        Answer(new
        {
            ok = true,
            header = true,
            srid = Epsg(layer),
            geometry = definition.GetGeomType().ToString(),
            fields,
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

                // <b>Not disposed, and that is the binding's rule rather than a leak.</b>
                // `GetGeometryRef` borrows the feature's own geometry; disposing it frees memory the
                // feature still owns, and the feature is disposed one line later anyway.
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
    private static object Layers(string archive)
    {
        using DataSource source = Open(archive);

        List<object> layers = [];

        for (int i = 0; i < source.GetLayerCount(); i++)
        {
            using Layer layer = source.GetLayerByIndex(i);
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
                    // rather than used: our schema has no alias column, and saying so is better than
                    // dropping it silently.
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

    /// <summary>Opens an archive, or says which one it could not open.</summary>
    private static DataSource Open(string archive) =>
        Ogr.Open(Vsi(archive), 0)
        ?? throw new InvalidOperationException(
            $"GDAL could not open '{archive}'. A File Geodatabase is a directory, so it arrives zipped "
            + "and is read through /vsizip/ — an archive that is not one, or one holding no "
            + "geodatabase, fails here.");

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

        return best is null ? null : inside + "/" + best;
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
