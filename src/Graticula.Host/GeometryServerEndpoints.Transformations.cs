using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Geometries;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace Graticula.Host;

/// <summary>
/// ArcGIS's <c>findTransformations</c>, and <c>project</c> through the transformation a caller names — ADR-160.
/// </summary>
/// <remarks>
/// <para>
/// <b>The list is generated, the work is the datastore's.</b> Which transformations join two references, how
/// accurate each is and where it applies come from <see cref="TransformationRegister"/>, read from PROJ's register
/// at build time (Q-100's fifth route, as ADR-060 did for axis order). Applying one is
/// <c>ST_TransformPipeline</c>, so the serving process still carries no PROJ.
/// </para>
/// <para>
/// <b>A transformation whose grid the datastore lacks is not offered.</b> It is tried once, at a point where it
/// applies, and remembered; a candidate that cannot run is not an answer to <i>which can I use</i> — D-32 from
/// the other side.
/// </para>
/// </remarks>
internal static partial class GeometryServerEndpoints
{
    /// <summary>
    /// Whether the datastore can apply each grid transformation, by code — bounded by the register, which is
    /// fixed at build time.
    /// </summary>
    private static readonly ConcurrentDictionary<int, bool> GridAvailable = new();

    /// <summary>
    /// <c>findTransformations</c>: the transformations from <c>inSR</c>'s geographic reference to <c>outSR</c>'s,
    /// most accurate first — one each, or two through WGS 84 as <c>geoTransforms</c> — limited by
    /// <c>numOfResults</c> (1 by default) and, given <c>extentOfInterest</c>, to those that apply there.
    /// </summary>
    private static async Task FindTransformationsAsync(HttpContext context, CancellationToken cancellation)
    {
        if (!TryForm(context, out IFormCollection form, out string? formError))
        {
            await Fail(context, formError!).ConfigureAwait(false);
            return;
        }

        if (!TrySrid(form, "inSR", out int inSr, out string? error) || !TrySrid(form, "outSR", out int outSr, out error))
        {
            await Fail(context, error!).ConfigureAwait(false);
            return;
        }

        int wanted = 1;

        if (Field(form, "numOfResults") is { } count
            && (!int.TryParse(count, NumberStyles.Integer, CultureInfo.InvariantCulture, out wanted) || wanted < 1))
        {
            await Fail(context, $"`numOfResults={count}` is how many transformations to return: a whole number, 1 or more.")
                .ConfigureAwait(false);
            return;
        }

        NpgsqlDataSource db = context.RequestServices.GetRequiredKeyedService<NpgsqlDataSource>(Program.DatastorePool);
        (int? fromGeo, int? toGeo) = (await GeographicOfAsync(db, inSr, cancellation).ConfigureAwait(false),
            await GeographicOfAsync(db, outSr, cancellation).ConfigureAwait(false));

        if (fromGeo is null || toGeo is null)
        {
            await Fail(context, $"{(fromGeo is null ? inSr : outSr)} is not a reference this server knows, so it has no "
                + "geographic reference to transform from or to.").ConfigureAwait(false);
            return;
        }

        Envelope? area = null;

        if (Field(form, "extentOfInterest") is { } extent)
        {
            if (!TryExtent(extent, inSr, out Envelope box, out int boxSrid, out error))
            {
                await Fail(context, error!).ConfigureAwait(false);
                return;
            }

            area = await DegreesAsync(db, box, boxSrid, cancellation).ConfigureAwait(false);
        }

        List<TransformationPath> usable = [];

        foreach (TransformationPath path in TransformationRegister.Paths(fromGeo.Value, toGeo.Value, area))
        {
            if (usable.Count >= wanted)
            {
                break;
            }

            if (await RunsAsync(db, path, cancellation).ConfigureAwait(false))
            {
                usable.Add(path);
            }
        }

        object Written(TransformationPath path) => path.Steps.Count == 1
            ? Step(path.Steps[0])
            : new { geoTransforms = path.Steps.Select(Step).ToArray() };

        // One asked for is the transformation itself, as ArcGIS answers it; more are a list.
        object document = wanted == 1 && usable.Count == 1
            ? Written(usable[0])
            : new { transformations = usable.Select(Written).ToArray() };

        await Respond(context, "findTransformations", document).ConfigureAwait(false);
    }

    private static object Step(TransformationStep step) => new
    {
        wkid = step.Transformation.Code,
        latestWkid = step.Transformation.Code,
        transformForward = step.Forward,
        name = step.Transformation.Name,
    };

    /// <summary>Whether the datastore can apply every grid a path reads — tried once each, then remembered.</summary>
    private static async Task<bool> RunsAsync(NpgsqlDataSource db, TransformationPath path, CancellationToken cancellation)
    {
        foreach (TransformationStep step in path.Steps.Where(s => s.Transformation.NeedsGrid))
        {
            int code = step.Transformation.Code;

            if (!GridAvailable.TryGetValue(code, out bool runs))
            {
                Envelope where = step.Transformation.Area;
                double x = (where.MinX + where.MaxX) / 2, y = (where.MinY + where.MaxY) / 2;

                try
                {
                    await using NpgsqlCommand probe = db.CreateCommand(
                        "select ST_IsEmpty(ST_TransformPipeline(ST_SetSRID(ST_MakePoint(@x, @y), @from), @urn, @to))");
                    probe.Parameters.AddWithValue("x", x);
                    probe.Parameters.AddWithValue("y", y);
                    probe.Parameters.AddWithValue("from", step.Transformation.Source);
                    probe.Parameters.AddWithValue("to", step.Transformation.Target);
                    probe.Parameters.AddWithValue("urn", step.Transformation.Urn);
                    runs = await probe.ExecuteScalarAsync(cancellation).ConfigureAwait(false) is false;
                }
                catch (PostgresException)
                {
                    runs = false;
                }

                GridAvailable[code] = runs;
            }

            if (!runs)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The geographic reference a reference is built on — itself when it is geographic — read from the datastore's
    /// own definition of it; null when the datastore does not know it.
    /// </summary>
    private static async Task<int?> GeographicOfAsync(NpgsqlDataSource db, int srid, CancellationToken cancellation)
    {
        await using NpgsqlCommand command = db.CreateCommand("select srtext from spatial_ref_sys where srid = @srid");
        command.Parameters.AddWithValue("srid", srid);

        if (await command.ExecuteScalarAsync(cancellation).ConfigureAwait(false) is not string wkt || wkt.Length == 0)
        {
            return null;
        }

        if (wkt.StartsWith("GEOGCS", StringComparison.Ordinal))
        {
            return srid;
        }

        int start = wkt.IndexOf("GEOGCS[", StringComparison.Ordinal);

        if (start < 0)
        {
            return null;
        }

        // The GEOGCS block, by its brackets; its own AUTHORITY is the last one directly inside it.
        int depth = 0, end = start;

        for (int i = start; i < wkt.Length; i++)
        {
            depth += wkt[i] == '[' ? 1 : wkt[i] == ']' ? -1 : 0;

            if (depth == 0 && wkt[i] == ']')
            {
                end = i;
                break;
            }
        }

        string block = wkt[start..(end + 1)];
        int authority = block.LastIndexOf("AUTHORITY[", StringComparison.Ordinal);

        if (authority < 0)
        {
            return null;
        }

        string[] parts = block[(authority + 10)..].Split(',', 2);
        string code = parts.Length == 2 ? new string(parts[1].Where(char.IsDigit).ToArray()) : string.Empty;
        return int.TryParse(code, NumberStyles.Integer, CultureInfo.InvariantCulture, out int geographic) ? geographic : null;
    }

    /// <summary>An <c>extentOfInterest</c>: an envelope, in its own reference or the request's <c>inSR</c>.</summary>
    private static bool TryExtent(string text, int fallback, out Envelope box, out int srid, out string? error)
    {
        box = default;
        srid = fallback;
        error = "`extentOfInterest` is an envelope: {\"xmin\":…,\"ymin\":…,\"xmax\":…,\"ymax\":…,\"spatialReference\":{\"wkid\":…}}.";

        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            JsonElement e = document.RootElement;

            if (e.ValueKind != JsonValueKind.Object
                || !e.TryGetProperty("xmin", out JsonElement xmin) || !e.TryGetProperty("ymin", out JsonElement ymin)
                || !e.TryGetProperty("xmax", out JsonElement xmax) || !e.TryGetProperty("ymax", out JsonElement ymax)
                || xmin.ValueKind != JsonValueKind.Number || ymin.ValueKind != JsonValueKind.Number
                || xmax.ValueKind != JsonValueKind.Number || ymax.ValueKind != JsonValueKind.Number)
            {
                return false;
            }

            if (e.TryGetProperty("spatialReference", out JsonElement reference) && reference.ValueKind == JsonValueKind.Object
                && (reference.TryGetProperty("latestWkid", out JsonElement wkid) || reference.TryGetProperty("wkid", out wkid))
                && wkid.TryGetInt32(out int given))
            {
                srid = given;
            }

            box = new Envelope(xmin.GetDouble(), ymin.GetDouble(), xmax.GetDouble(), ymax.GetDouble());
            error = null;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>An envelope in degrees of WGS 84, for comparing with where transformations apply.</summary>
    private static async Task<Envelope?> DegreesAsync(NpgsqlDataSource db, Envelope box, int srid, CancellationToken cancellation)
    {
        try
        {
            await using NpgsqlCommand command = db.CreateCommand(
                "select ST_XMin(e), ST_YMin(e), ST_XMax(e), ST_YMax(e) from "
                + "(select ST_Transform(ST_MakeEnvelope(@x0, @y0, @x1, @y1, @srid), 4326)::box2d as e) t");
            command.Parameters.AddWithValue("x0", box.MinX);
            command.Parameters.AddWithValue("y0", box.MinY);
            command.Parameters.AddWithValue("x1", box.MaxX);
            command.Parameters.AddWithValue("y1", box.MaxY);
            command.Parameters.AddWithValue("srid", srid);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellation).ConfigureAwait(false);
            return await reader.ReadAsync(cancellation).ConfigureAwait(false)
                ? new Envelope(reader.GetDouble(0), reader.GetDouble(1), reader.GetDouble(2), reader.GetDouble(3))
                : null;
        }
        catch (PostgresException)
        {
            return null;
        }
    }

    /// <summary>
    /// The path a <c>project</c> request's <c>transformation</c> names — a wkid with <c>transformForward</c>, or
    /// <c>{"wkid":…}</c>, or <c>{"geoTransforms":[{"wkid":…,"transformForward":…},…]}</c> — checked to lead from
    /// <paramref name="fromGeo"/> to <paramref name="toGeo"/>.
    /// </summary>
    private static bool TryPinnedPath(
        string text, string? forward, int fromGeo, int toGeo, out TransformationPath? path, out string? error)
    {
        path = null;
        error = null;
        List<(int Code, bool Forward)> asked = [];
        bool defaultForward = !string.Equals(forward, "false", StringComparison.OrdinalIgnoreCase);

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int bare))
        {
            asked.Add((bare, defaultForward));
        }
        else
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(text);
                JsonElement root = document.RootElement;
                IEnumerable<JsonElement> steps = root.TryGetProperty("geoTransforms", out JsonElement list) && list.ValueKind == JsonValueKind.Array
                    ? list.EnumerateArray()
                    : [root];

                foreach (JsonElement step in steps)
                {
                    if (!(step.TryGetProperty("wkid", out JsonElement wkid) || step.TryGetProperty("latestWkid", out wkid)) || !wkid.TryGetInt32(out int code))
                    {
                        error = $"`transformation={text}` names no wkid.";
                        return false;
                    }

                    asked.Add((code, step.TryGetProperty("transformForward", out JsonElement f) ? f.ValueKind != JsonValueKind.False : defaultForward));
                }
            }
            catch (JsonException)
            {
                error = $"`transformation={text}` is a wkid, or {{\"wkid\":…}}, or {{\"geoTransforms\":[…]}}.";
                return false;
            }
        }

        List<TransformationStep> chosen = [];
        int at = fromGeo;

        foreach ((int code, bool isForward) in asked)
        {
            if (TransformationRegister.Find(code) is not { } found)
            {
                error = $"{code} is not a datum transformation this server knows. findTransformations lists those between two references.";
                return false;
            }

            int start = isForward ? found.Source : found.Target, end = isForward ? found.Target : found.Source;

            if (start != at)
            {
                error = $"{found.Name} ({code}) goes from {start} to {end}{(isForward ? "" : " used backwards")}, and the geometries "
                    + $"are in {at} at that step. findTransformations lists the ways from {fromGeo} to {toGeo}.";
                return false;
            }

            chosen.Add(new TransformationStep(found, isForward));
            at = end;
        }

        if (at != toGeo)
        {
            error = $"The transformation given ends in {at}, and outSR's geographic reference is {toGeo}.";
            return false;
        }

        path = new TransformationPath(chosen);
        return true;
    }

    /// <summary>
    /// Geometries moved from <paramref name="inSr"/> to <paramref name="outSr"/> through a pinned path: to the input's
    /// geographic reference, along each transformation, then to the output — one statement, in order.
    /// </summary>
    private static async Task<(IReadOnlyList<Geometry>? Moved, string? Error)> ProjectThroughAsync(
        NpgsqlDataSource db, List<Geometry> geometries, int inSr, int fromGeo, TransformationPath path, int outSr,
        CancellationToken cancellation)
    {
        string expression = "ST_Transform(ST_SetSRID(ST_GeomFromWKB(t.g), @in), @from_geo)";

        for (int i = 0; i < path.Steps.Count; i++)
        {
            string function = path.Steps[i].Forward ? "ST_TransformPipeline" : "ST_InverseTransformPipeline";
            expression = $"{function}({expression}, @urn{i}, @to{i})";
        }

        await using NpgsqlCommand command = db.CreateCommand(
            $"select ST_AsBinary(ST_Transform({expression}, @out)) from unnest(@geometries) with ordinality as t(g, n) order by t.n");
        command.Parameters.AddWithValue("in", inSr);
        command.Parameters.AddWithValue("from_geo", fromGeo);
        command.Parameters.AddWithValue("out", outSr);

        for (int i = 0; i < path.Steps.Count; i++)
        {
            TransformationStep step = path.Steps[i];
            command.Parameters.AddWithValue("urn" + i.ToString(CultureInfo.InvariantCulture), step.Transformation.Urn);
            command.Parameters.AddWithValue("to" + i.ToString(CultureInfo.InvariantCulture),
                step.Forward ? step.Transformation.Target : step.Transformation.Source);
        }

        command.Parameters.Add(new NpgsqlParameter("geometries", NpgsqlDbType.Array | NpgsqlDbType.Bytea)
        {
            Value = geometries.Select(g => WkbWriter.ToArray(g)).ToArray(),
        });

        try
        {
            List<Geometry> moved = new(geometries.Count);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellation).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellation).ConfigureAwait(false))
            {
                moved.Add(WkbReader.Read((byte[])reader.GetValue(0)));
            }

            return (moved, null);
        }
        catch (PostgresException e)
        {
            return (null, $"The datastore could not apply {string.Join(" then ", path.Steps.Select(s => s.Transformation.Name))}: "
                + $"{e.MessageText}. A transformation that reads a grid needs the grid installed beside PROJ.");
        }
    }
}
