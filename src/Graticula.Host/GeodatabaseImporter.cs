using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Features;
using Graticula.Formats;
using Graticula.Geometries;
using Graticula.Platform.Admin;
using Graticula.Platform.Identity;
using Graticula.Platform.Jobs;
using Graticula.Providers.PostGis;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Graticula.Host;

/// <summary>
/// Publishes the layers chosen from a geodatabase into one service.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-038](../../docs/adr/ADR-038-how-a-geodatabase-becomes-a-service.md), on the owner's rule:</b>
/// *"servis ve katman ayrı şeyler. bir serviste n katman olabilir."* One archive becomes one service
/// holding N layers, each its own table. Which sounds like what the product already did and is not —
/// every other route into hosted data makes one service holding one layer, so the two words have been
/// interchangeable in practice until now.
/// </para>
/// <para>
/// <b>Per layer, and reported per layer.</b> Fifty-five layers is fifty-five chances to fail, and a job
/// that says only *failed* after importing forty is a job nobody can act on — which is what
/// <see cref="IJobStore"/> refuses. Each layer's outcome goes into the job's detail as it happens.
/// </para>
/// <para>
/// <b>What this does not yet do, and ADR-038 condition 1 is the record of it.</b> The features are
/// streamed from the reader and then <b>held in memory</b> as an <see cref="ImportedDataset"/>, because
/// that is what <c>PostGisImporter</c> takes. So the pipe streams and the importer does not, and the
/// 1,000,000-feature ceiling has not moved for this path — it has only stopped being a *format's*
/// ceiling. The owner's largest layer is 3,659 features, so nothing here is bounded by it today; the
/// condition stays open and says so rather than being quietly discharged.
/// </para>
/// </remarks>
internal sealed class GeodatabaseImporter : BackgroundService
{
    /// <summary>How long one layer has to arrive from the reader.</summary>
    /// <remarks>
    /// <b>Per layer rather than per archive, and generous.</b> Ten minutes against a measured 0.3 s for
    /// the owner's largest — three orders of magnitude, which is the same shape of headroom the
    /// inspection's two minutes has. It bounds a stuck process, not a large one.
    /// </remarks>
    public static readonly TimeSpan Deadline = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(2);

    /// <summary>How a job's detail is read back.</summary>
    /// <remarks>
    /// <b>Case-insensitive, because the default is not, and the first real publish was the proof.</b>
    /// The endpoint writes an anonymous object — `archive`, `service`, `layers` — and
    /// <c>JsonSerializer</c>'s default options match property names exactly. So every constructor
    /// parameter on <see cref="Request"/> took its default: no layers, and a job that reported
    /// *published 0 of 0* after eight were asked for. It failed rather than claiming success, which is
    /// the only reason this was a puzzle for two minutes instead of a service quietly missing its data.
    /// </remarks>
    private static readonly JsonSerializerOptions AsWritten = new(JsonSerializerDefaults.Web);

    private readonly IJobStore _jobs;
    private readonly GeodatabaseReader _reader;
    private readonly ImportScratch _scratch;
    private readonly PostGisImporter _importer;
    private readonly IAdminCatalog _catalog;
    private readonly ILogger<GeodatabaseImporter> _log;

    public GeodatabaseImporter(
        IJobStore jobs,
        GeodatabaseReader reader,
        ImportScratch scratch,
        PostGisImporter importer,
        IAdminCatalog catalog,
        ILogger<GeodatabaseImporter> log)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(scratch);
        ArgumentNullException.ThrowIfNull(importer);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(log);

        _jobs = jobs;
        _reader = reader;
        _scratch = scratch;
        _importer = importer;
        _catalog = catalog;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        if (!_reader.Available)
        {
            return;
        }

        while (!stopping.IsCancellationRequested)
        {
            JobRecord? job;

            try
            {
                job = await _jobs.ClaimAsync(JobKind.GeodatabaseImport, stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception unreachable)
            {
                Log.InspectorClaimFailed(_log, unreachable);
                await Wait(stopping).ConfigureAwait(false);
                continue;
            }

            if (job is null)
            {
                await Wait(stopping).ConfigureAwait(false);
                continue;
            }

            await RunAsync(job, stopping).ConfigureAwait(false);
        }
    }

    private async Task RunAsync(JobRecord job, CancellationToken stopping)
    {
        Request asked;

        try
        {
            asked = JsonSerializer.Deserialize<Request>(job.Detail ?? "{}", AsWritten)
                ?? throw new InvalidOperationException("The job carries no request.");

            if (asked.Layers is not { Count: > 0 })
            {
                throw new InvalidOperationException(
                    "This job names no layers to publish, which the endpoint refuses to create — so "
                    + "either it was written by something else, or this server and the endpoint "
                    + "disagree about the shape of a request.");
            }

            if (string.IsNullOrWhiteSpace(asked.Service))
            {
                throw new InvalidOperationException(
                    "This job names no service to publish into, which the endpoint refuses to create.");
            }
        }
        catch (Exception malformed)
        {
            await _jobs.FinishAsync(
                job.Id, JobStatus.Failed, null,
                $"This job's own request could not be read: {malformed.Message}", stopping)
                .ConfigureAwait(false);

            return;
        }

        string archive = _scratch.PathFor(asked.Archive);
        List<object> done = [];
        int landed = 0;

        try
        {
            foreach (string layer in asked.Layers ?? [])
            {
                if (stopping.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    Landed made = await PublishAsync(asked, archive, layer, stopping)
                        .ConfigureAwait(false);

                    landed++;

                    // <b>`flattened` is in the report even when it is zero</b>, because *this layer
                    // kept its elevation* and *this server did not look* are different answers and a
                    // missing field cannot tell them apart.
                    done.Add(new
                    {
                        layer,
                        published = true,
                        rows = made.Rows,
                        flattened = made.Flattened,

                        // <b>Only when it is the whole story.</b> A layer with rows says nothing about
                        // being schema-only; a layer with none needs to, because otherwise *published,
                        // 0 features* reads as a failure somebody should investigate.
                        schemaOnly = made.Rows == 0 ? made.Declared : (int?)null,
                    });
                }
                catch (Exception refused)
                {
                    // <b>Named, and the rest continues.</b> One feature class with a geometry PostGIS
                    // refuses must not cost the other fifty-four — and *which* it was is the only thing
                    // that makes the failure actionable.
                    done.Add(new { layer, published = false, why = refused.Message });
                }

                // Progress after each, because a fifty-five layer import that reports nothing for
                // twenty minutes is indistinguishable from one that has stopped.
                await _jobs.ProgressAsync(
                    job.Id,
                    (int)Math.Round(100.0 * done.Count / Math.Max(1, (asked.Layers ?? []).Count)),
                    stopping).ConfigureAwait(false);
            }

            bool all = done.Count > 0 && landed == done.Count;

            await _jobs.FinishAsync(
                job.Id,
                all ? JobStatus.Done : JobStatus.Failed,
                JsonSerializer.Serialize(new
                {
                    service = asked.Service,
                    folder = asked.Folder,
                    published = landed,
                    of = done.Count,
                    layers = done,
                }),
                all
                    ? null
                    : $"{landed} of {done.Count} layers were published. The rest are named in the "
                      + "detail with the reason each was refused.",
                stopping).ConfigureAwait(false);

            Log.ImportFinished(_log, job.Id, landed, done.Count);
        }
        catch (Exception failed)
        {
            await _jobs.FinishAsync(job.Id, JobStatus.Failed, null, failed.Message, stopping)
                .ConfigureAwait(false);
        }
        finally
        {
            // <b>The archive goes when the import does, either way.</b> The inspection keeps it so the
            // operator can choose from what it found; this is the other end of that, and without it a
            // decision nobody made would hold a file for ever.
            _scratch.Release(archive);
        }
    }

    /// <summary>What one published layer turned out to be.</summary>
    /// <param name="Rows">How many features landed.</param>
    /// <param name="Flattened">How many of them arrived with a Z this server does not store.</param>
    /// <param name="Declared">
    /// How many columns came from the archive's own declaration rather than from reading rows — zero
    /// unless the layer was empty, which is D-106's case and worth reporting because *published with no
    /// features* and *failed* look the same from a distance.
    /// </param>
    private readonly record struct Landed(int Rows, int Flattened, int Declared = 0);

    /// <summary>Streams one layer out of the archive and publishes it into the service.</summary>
    private async Task<Landed> PublishAsync(
        Request asked, string archive, string layer, CancellationToken stopping)
    {
        List<ImportedFeature> features = [];
        Dictionary<string, InferredColumn> columns = new(StringComparer.Ordinal);
        // <b>The first geometry decides, because `GeometryKind` has no unknown member.</b>
        // `GeoJsonFeatures` unifies the kinds it sees the same way; a layer whose features disagree is
        // the importer's problem to refuse, not this reader's to average.
        GeometryKind? kind = null;

        // <b>Why the first geometry was refused, kept rather than discarded.</b> The first version
        // discarded it, and the refusal then said only *none of them carries a geometry this server
        // could read* for all eight of the archive's layers — true, useless, and it cost a measurement
        // to turn into a reason. Whatever the geometry reader said is what a person needs.
        string? unread = null;
        int shapeless = 0;

        // <b>How many features arrived with a Z this server does not store.</b> Six of the owner's
        // eight layers are `25D`, so this is the common case rather than an oddity, and
        // `docs/geometry-crs-policy.md` is explicit that a lossy read is a fact the layer has to
        // carry: *lossy on read means not writable*. It is reported per layer in the job's detail —
        // D-105 is the entry for what this server does not yet do with it.
        int flattened = 0;

        (JsonDocument? header, JsonDocument? trailer) = await _reader.StreamAsync(
            new { op = "features", archive, layer },
            Deadline,
            line =>
            {
                JsonElement root = line.RootElement;

                Geometry? geometry = null;

                if (!root.TryGetProperty("g", out JsonElement shape)
                    || shape.ValueKind != JsonValueKind.String)
                {
                    shapeless++;
                }
                else
                {
                    try
                    {
                        // <b>WKB rather than GeoJSON on this wire, and ADR-038 §4B records the
                        // reversal.</b> GeoJSON's coordinates are WGS 84 by definition, which is why
                        // `GeoJsonGeometry` refuses a position outside ±180/±90 — and the first real
                        // archive is EPSG:2952, so every feature in it was refused by a check that was
                        // doing its job. WKB has no opinion about the coordinate system; the header
                        // declares it, and the importer decides what to store.
                        geometry = WkbReader.Read(
                            Convert.FromBase64String(shape.GetString() ?? string.Empty),
                            out bool dropped);

                        if (dropped)
                        {
                            flattened++;
                        }

                        kind ??= geometry.Kind;
                    }
                    catch (Exception refused)
                        when (refused is WkbFormatException or FormatException
                              or ArgumentException or NotSupportedException)
                    {
                        unread ??= refused.Message;
                    }
                }

                Dictionary<string, JsonElement> values = new(StringComparer.Ordinal);

                if (root.TryGetProperty("v", out JsonElement carried)
                    && carried.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty property in carried.EnumerateObject())
                    {
                        // Cloned, because the document this element belongs to is disposed when the
                        // line is — and a dataset holding freed memory is a defect that looks like data
                        // corruption.
                        JsonElement kept = property.Value.Clone();

                        values[property.Name] = kept;

                        if (!columns.TryGetValue(property.Name, out InferredColumn? column))
                        {
                            column = new InferredColumn { Name = property.Name };
                            columns[property.Name] = column;
                        }

                        column.Observe(kept);
                    }
                }

                features.Add(new ImportedFeature(geometry, values));
            },
            stopping).ConfigureAwait(false);

        int srid = header is not null
            && header.RootElement.TryGetProperty("srid", out JsonElement code)
            && code.ValueKind == JsonValueKind.Number
            ? code.GetInt32()
            : 0;

        // <b>The header's own schema, kept before the document is disposed.</b> It is what makes an
        // empty feature class publishable — see below — and reading it out here rather than inside the
        // branch keeps the lifetime of `header` in one place.
        List<FieldDescription> declared = [];
        GeometryKind? declaredKind = null;

        if (header is not null)
        {
            if (header.RootElement.TryGetProperty("fields", out JsonElement fields)
                && fields.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement field in fields.EnumerateArray())
                {
                    if (field.TryGetProperty("name", out JsonElement named)
                        && named.GetString() is { Length: > 0 } name)
                    {
                        declared.Add(new FieldDescription(
                            name,
                            FieldTypeOf(field.TryGetProperty("type", out JsonElement declaredType)
                                ? declaredType.GetString()
                                : null),

                            // <b>Nullable, always, and not because we do not know.</b> OGR reports a
                            // field's nullability and a geodatabase's *required* flag is Esri's own
                            // notion, enforced by Esri's editors rather than by the data — so a column
                            // declared `not null` here would refuse rows the source itself holds. The
                            // one import path that does honour a `not null` is the designed one, where
                            // a person chose it.
                            Nullable: true,
                            MaxLength: null));
                    }
                }
            }

            declaredKind = GeometryKindOf(
                header.RootElement.TryGetProperty("geometry", out JsonElement geometry)
                    ? geometry.GetString()
                    : null);
        }

        header?.Dispose();
        trailer?.Dispose();

        if (srid <= 0)
        {
            throw new InvalidOperationException(
                $"'{layer}' declares a coordinate system this server could not resolve to an EPSG code, "
                + "so it is refused rather than stored as a guess. ADR-024's rule, applied to a "
                + "geodatabase: a layer must not declare a system it is not in.");
        }

        if (features.Count == 0)
        {
            // <b>D-106: the archive declares its schema, so an empty feature class is publishable.</b>
            // Every other import path in this server infers columns from the rows it read, which is the
            // only thing a GeoJSON file offers — so an empty layer was refused, and the owner's
            // 55-layer archive holds one (`AECOM_Monitoring_Well_Inventory`). A geodatabase is not
            // GeoJSON: the reader's header carries the field list with types and the geometry type,
            // which is exactly what `DefineAsync` takes. ArcGIS publishes these; a survey layer
            // exported before anybody had filled it in is the ordinary case.
            //
            // <b>Refused only when the header is silent.</b> No geometry type means there is nothing to
            // declare the column as, and a layer with no fields and no geometry is not a layer.
            if (declaredKind is null)
            {
                throw new InvalidOperationException(
                    $"'{layer}' holds no features and declares no geometry type this server stores, "
                    + "so there is nothing to create the geometry column as. An attachment table "
                    + "looks like this, and the inspection lists those and says so.");
            }

            ImportResult defined = await _importer
                .DefineAsync(declared, declaredKind.Value, srid, layer, stopping)
                .ConfigureAwait(false);

            await _catalog.PublishLayerAsync(
                new LayerPublication(
                    layer,
                    asked.Datastore,
                    defined.SchemaName,
                    defined.TableName,
                    "geom",
                    "objectid",
                    "objectid",
                    srid,
                    declaredKind.Value,
                    Enum.TryParse(asked.Sharing, ignoreCase: true, out SharingScope emptyScope)
                        ? emptyScope
                        : SharingScope.Private,
                    ServiceName: asked.Service,
                    Folder: asked.Folder ?? "hosted"),
                asked.Owner,
                stopping).ConfigureAwait(false);

            return new Landed(0, 0, Declared: declared.Count);
        }

        if (kind is null)
        {
            throw new InvalidOperationException(
                $"'{layer}' holds {features.Count} feature(s) and none of them carries a geometry this "
                + "server could read, so there is no geometry type to declare. "
                + (unread is not null
                    ? $"The first one it refused: {unread}"
                    : $"{shapeless} of them carry no geometry at all, which is what an attachment or "
                      + "relationship table looks like — the inspection lists those and says so."));
        }

        ImportedDataset dataset = new(features, [.. columns.Values], kind.Value, srid);

        ImportResult made = await _importer
            .ImportAsync(dataset, layer, stopping).ConfigureAwait(false);

        // <b>Into the named service, at the next free index.</b> This is the mechanism the
        // registered-table form has always used and the geodatabase import is its first other caller —
        // which is what makes one archive one service rather than N.
        await _catalog.PublishLayerAsync(
            new LayerPublication(
                layer,
                asked.Datastore,
                made.SchemaName,
                made.TableName,
                "geom",
                "objectid",
                "objectid",
                made.StoredSrid,
                dataset.GeometryType,

                // The scope arrives as the word the API takes and is parsed here rather than trusted:
                // an unrecognised value becomes `private`, which is the narrowest, for the reason
                // `ReadVisibility` gives — a scope this build does not understand must not read as the
                // widest.
                Enum.TryParse(asked.Sharing, ignoreCase: true, out SharingScope scope)
                    ? scope
                    : SharingScope.Private,
                ServiceName: asked.Service,
                Folder: asked.Folder ?? "hosted"),
            asked.Owner,
            stopping).ConfigureAwait(false);

        return new Landed(features.Count, flattened);
    }

    /// <summary>
    /// An OGR field type name as this server's own field type.
    /// </summary>
    /// <remarks>
    /// <b>The names are GDAL's `OGR_GetFieldTypeName` output, and the fallback is text.</b> A type this
    /// map does not know becomes `Text`, which is what `FieldType.Unknown` already means everywhere
    /// else in this server — *rendered as text* — rather than a refusal. A geodatabase's exotic field
    /// types (raster, blob, XML) do carry values a client can be shown; refusing the whole layer over a
    /// column nobody asked about would be the wrong trade.
    /// </remarks>
    private static FieldType FieldTypeOf(string? ogr) => ogr switch
    {
        "Integer" => FieldType.Integer,
        "Integer64" => FieldType.BigInteger,
        "Real" => FieldType.Double,
        "Date" or "Time" or "DateTime" => FieldType.Date,
        "Binary" => FieldType.Binary,

        // Includes "String", "IntegerList", "RealList", "StringList" and anything new: a list has no
        // column type here and its text form is what a client can read.
        _ => FieldType.Text,
    };

    /// <summary>
    /// A WKB geometry type name as this server's own geometry kind, or null when it stores none.
    /// </summary>
    /// <remarks>
    /// <b>The `25D` suffix is dropped rather than refused</b>, because that is what the import does with
    /// the geometry itself: `WkbReader` drops Z and says it did (D-107). A layer declared
    /// `wkbMultiPolygon25D` becomes a 2D MultiPolygon column, which is the same answer the non-empty
    /// path arrives at one feature at a time.
    ///
    /// <b>`wkbNone` and anything unrecognised are null</b> — an attachment table, or a curve type
    /// ADR-005 §3.3c refuses. Null is a refusal with a sentence, not a guess.
    /// </remarks>
    private static GeometryKind? GeometryKindOf(string? wkb) =>
        (wkb ?? string.Empty).Replace("25D", string.Empty, StringComparison.Ordinal) switch
        {
            "wkbPoint" => GeometryKind.Point,
            "wkbMultiPoint" => GeometryKind.MultiPoint,
            "wkbLineString" => GeometryKind.LineString,
            "wkbMultiLineString" => GeometryKind.MultiLineString,
            "wkbPolygon" => GeometryKind.Polygon,
            "wkbMultiPolygon" => GeometryKind.MultiPolygon,
            _ => null,
        };

    private static async Task Wait(CancellationToken stopping)
    {
        try
        {
            await Task.Delay(Idle, stopping).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    /// <summary>What the endpoint put in the job, read back here.</summary>
    /// <remarks>
    /// <b>The archive is an id, not a path.</b> `ImportScratch` names the file after it, so the path is
    /// computed rather than carried — and a job's detail is returned to its owner verbatim, which is not
    /// somewhere this server's directory layout belongs.
    /// </remarks>
    private sealed record Request(
        Guid Archive,
        Guid Owner,
        Guid Datastore,
        string Service,
        string? Folder,
        string? Sharing,
        List<string>? Layers);
}
