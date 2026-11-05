using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Postgres;
using Graticula.Platform.Identity;
using Graticula.Scenes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>What a caller sends to make a scene layer from a layer (ADR-269 §5.3).</summary>
/// <param name="Service">The feature service's name.</param>
/// <param name="Folder">Its folder.</param>
/// <param name="Layer">The layer's index in it.</param>
/// <param name="HeightField">The field holding each building's height in metres, none drawing the footprints flat; for
/// a point layer, each point's height above the ground, none standing the points at their own z, or on the ground.</param>
/// <param name="BaseField">The field holding the height a building's floor stands at, or none for the ground (0). A point
/// layer has none.</param>
/// <param name="Title">The scene layer's title; the layer's otherwise.</param>
/// <param name="Colour">The colour as "#rrggbb"; a light grey for buildings and a blue for points otherwise.</param>
/// <param name="HeightScale">Metres per unit of the height field — 3 for a field of floors; 1 otherwise.</param>
/// <param name="OnTheGround">A point layer with z placed on the ground instead of at its z.</param>
internal sealed record SceneFromLayerRequest(
    string? Service, string? Folder, int? Layer, string? HeightField, string? BaseField, string? Title, string? Colour,
    double? HeightScale = null, bool? OnTheGround = null);

internal static partial class AdminEndpoints
{
    /// <summary>The most buildings one request turns into a scene layer — the rest wait for ADR-269's revisit.</summary>
    internal const int MaximumSceneBuildings = 200_000;

    /// <summary>The most points one request turns into a scene layer: sixteen bytes each before gzip, a node of a
    /// few thousand at a time.</summary>
    internal const int MaximumScenePoints = 1_000_000;

    private static void MapScenes(WebApplication app)
    {
        // ADR-269 §5.3: a polygon layer's footprints, extruded, or a point layer's points, written as a scene layer
        // package the caller owns.
        app.MapPost("/content/scenes", CreateSceneFromLayerAsync);
    }

    /// <summary>
    /// Makes a 3D object scene layer from a polygon layer the caller can read, or a point scene layer from a point
    /// layer (D-386), and keeps it as a package they own.
    /// </summary>
    /// <remarks>
    /// <b>Synchronous, and bounded.</b> A district's buildings take a second; the request reads at most
    /// <see cref="MaximumSceneBuildings"/> features in pages and the package must fit the document limit, so a caller
    /// is told what stopped it rather than left waiting on a job nobody can see. The package is a snapshot: an edit to
    /// the layer reaches the scene when it is made again (ADR-269 §6).
    /// </remarks>
    private static async Task<IResult> CreateSceneFromLayerAsync(
        HttpContext context, SceneFromLayerRequest request, CatalogFallback catalog, ServiceContexts contexts,
        IDocumentStore documents, IAuditLog audit, CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentCreate).ConfigureAwait(false))
        {
            return Results.Empty;
        }

        if (string.IsNullOrWhiteSpace(request.Service) || request.Layer is not { } index)
        {
            return Refuse(400, "Name the layer: its service, its folder if it has one, and its layer index.");
        }

        if (await ServiceLookup.ServiceAsync(context, catalog, request.Service, cancellation, request.Folder ?? string.Empty).ConfigureAwait(false)
            is not { } service)
        {
            return Results.Empty;
        }

        if (service.LayerOrTable(index) is not { } layer)
        {
            return Refuse(404, $"'{service.QualifiedName}' has no layer {index}.");
        }

        bool points = layer.GeometryType is GeometryKind.Point;
        if (!points && layer.GeometryType is not (GeometryKind.Polygon or GeometryKind.MultiPolygon))
        {
            return Refuse(400, $"'{layer.Definition.Name}' is neither a polygon nor a point layer. A scene layer is made from footprints or from points.");
        }

        if (points && !string.IsNullOrWhiteSpace(request.BaseField))
        {
            return Refuse(400, "A point layer has no base: its height field is the point's height above the ground.");
        }

        double scale = request.HeightScale ?? 1;
        if (!double.IsFinite(scale) || scale <= 0 || scale > 1000)
        {
            return Refuse(400, "heightScale is the metres one unit of the height field stands for: a number above 0, at most 1000.");
        }

        int[] colour = points ? [0, 112, 192] : [220, 220, 215];
        if (!string.IsNullOrWhiteSpace(request.Colour))
        {
            string hex = request.Colour.Trim().TrimStart('#');
            if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
            {
                return Refuse(400, $"'{request.Colour}' is not a colour; send it as #rrggbb.");
            }

            colour = [(rgb >> 16) & 0xff, (rgb >> 8) & 0xff, rgb & 0xff];
        }

        (IFeatureSource source, LayerDescription described) = await contexts.GetAsync(layer, cancellation).ConfigureAwait(false);

        if (points)
        {
            return await CreatePointSceneAsync(
                context, request, service, layer, index, source, described, scale, colour, documents, audit, cancellation).ConfigureAwait(false);
        }

        FieldDescription? height = null, floor = null;
        if (!string.IsNullOrWhiteSpace(request.HeightField))
        {
            height = described.Fields.FirstOrDefault(f => f.Name.Equals(request.HeightField, StringComparison.OrdinalIgnoreCase));
            if (height is null || !Numeric(height.Value.Type))
            {
                return Refuse(400, $"'{request.HeightField}' is not a number field of '{layer.Definition.Name}'.");
            }
        }

        if (!string.IsNullOrWhiteSpace(request.BaseField))
        {
            floor = described.Fields.FirstOrDefault(f => f.Name.Equals(request.BaseField, StringComparison.OrdinalIgnoreCase));
            if (floor is null || !Numeric(floor.Value.Type))
            {
                return Refuse(400, $"'{request.BaseField}' is not a number field of '{layer.Definition.Name}'.");
            }
        }

        List<FieldDescription> carried = Carried(layer, described);

        List<SceneBuilding> buildings = [];
        int page = FeatureQuery.MaximumLimit;

        // Named, because a query that names no fields answers with none.
        string[] asked = [.. carried.Select(f => f.Name)
            .Concat(new[] { height?.Name, floor?.Name }.OfType<string>()).Distinct(StringComparer.OrdinalIgnoreCase)];

        for (int offset = 0; ; offset += page)
        {
            int read = 0;
            await foreach (Feature feature in source.ReadAsync(new FeatureQuery(page, fields: asked, offset: offset, outSrid: 4326), cancellation).ConfigureAwait(false))
            {
                read++;
                if (!long.TryParse(feature.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id))
                {
                    id = buildings.Count + 1;
                }

                List<IReadOnlyList<IReadOnlyList<(double, double)>>> parts = feature.Geometry switch
                {
                    Polygon polygon => [Rings(polygon)],
                    MultiPolygon multi => [.. multi.Parts.Select(Rings)],
                    _ => [],
                };

                if (parts.Count == 0)
                {
                    continue;
                }

                double tall = height is { } h ? Number(feature[h.Name]) * scale : 0;
                double bottom = floor is { } b ? Number(feature[b.Name]) : 0;
                buildings.Add(new SceneBuilding(id, parts, bottom, tall, [.. carried.Select(f => feature[f.Name])]));
            }

            if (read < page || buildings.Count >= MaximumSceneBuildings)
            {
                break;
            }
        }

        if (buildings.Count == 0)
        {
            return Refuse(400, $"'{layer.Definition.Name}' has no footprints to build.");
        }

        if (buildings.Count > MaximumSceneBuildings)
        {
            return Refuse(413, $"'{layer.Definition.Name}' has more than {MaximumSceneBuildings:N0} footprints, more than one scene layer is made from here.");
        }

        string title = string.IsNullOrWhiteSpace(request.Title) ? $"{layer.Definition.Name} (3D)" : request.Title.Trim();
        byte[] package = SceneLayerPackageWriter.Write(
            title, [.. carried.Select(f => new SceneField(f.Name, f.Type, f.Alias))], buildings, colour, DateTimeOffset.UtcNow);

        return await KeepSceneAsync(
            context, service, index, title, $"{buildings.Count:N0} buildings", "buildings", buildings.Count, package, documents, audit, cancellation)
            .ConfigureAwait(false);

    }

    private static IResult Refuse(int status, string message) =>
        Results.Json(new { error = new { code = status, message, details = Array.Empty<string>() } }, statusCode: status);

    /// <summary>The fields a scene layer carries: every one but the geometry and the object id.</summary>
    private static List<FieldDescription> Carried(PublishedLayer layer, LayerDescription described) =>
        [.. described.Fields.Where(f =>
            !f.Name.Equals(layer.Definition.GeometryColumn, StringComparison.OrdinalIgnoreCase)
            && !f.Name.Equals(layer.Definition.IdentityColumn, StringComparison.OrdinalIgnoreCase)
            && f.Type is not (FieldType.Binary or FieldType.Unknown or FieldType.Boolean))];

    /// <summary>
    /// A point layer as a point scene layer (D-386): each point at its height above the ground when a field gives one,
    /// at its own z when it has one, and on the ground otherwise.
    /// </summary>
    private static async Task<IResult> CreatePointSceneAsync(
        HttpContext context, SceneFromLayerRequest request, PublishedService service, PublishedLayer layer, int index, IFeatureSource source,
        LayerDescription described, double scale, int[] colour, IDocumentStore documents, IAuditLog audit,
        CancellationToken cancellation)
    {
        FieldDescription? height = null;
        if (!string.IsNullOrWhiteSpace(request.HeightField))
        {
            height = described.Fields.FirstOrDefault(f => f.Name.Equals(request.HeightField, StringComparison.OrdinalIgnoreCase));
            if (height is null || !Numeric(height.Value.Type))
            {
                return Refuse(400, $"'{request.HeightField}' is not a number field of '{layer.Definition.Name}'.");
            }
        }

        List<FieldDescription> carried = Carried(layer, described);
        string[] asked = [.. carried.Select(f => f.Name).Concat(new[] { height?.Name }.OfType<string>()).Distinct(StringComparer.OrdinalIgnoreCase)];
        List<ScenePoint> made = [];
        bool anyZ = false;
        int page = FeatureQuery.MaximumLimit;

        for (int offset = 0; ; offset += page)
        {
            int read = 0;
            await foreach (Feature feature in source.ReadAsync(new FeatureQuery(page, fields: asked, offset: offset, outSrid: 4326), cancellation).ConfigureAwait(false))
            {
                read++;
                if (feature.Geometry is not Point { IsEmpty: false } point)
                {
                    continue;
                }

                if (!long.TryParse(feature.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id))
                {
                    id = made.Count + 1;
                }

                if (id is < int.MinValue or > int.MaxValue)
                {
                    return Refuse(400, $"'{layer.Definition.Name}' has object id {id}; a point scene layer holds 32-bit ids only.");
                }

                bool ground = request.OnTheGround == true;
                anyZ |= point.Z is not null && !ground;
                double z = height is { } hf ? Number(feature[hf.Name]) * scale : ground ? 0 : point.Z ?? 0;
                made.Add(new ScenePoint(id, point.X, point.Y, double.IsFinite(z) ? z : 0, [.. carried.Select(f => feature[f.Name])]));
            }

            if (read < page || made.Count > MaximumScenePoints)
            {
                break;
            }
        }

        if (made.Count == 0)
        {
            return Refuse(400, $"'{layer.Definition.Name}' has no points to place.");
        }

        if (made.Count > MaximumScenePoints)
        {
            return Refuse(413, $"'{layer.Definition.Name}' has more than {MaximumScenePoints:N0} points, more than one scene layer is made from here.");
        }

        SceneElevation elevation = height is null && anyZ ? SceneElevation.AbsoluteHeight : SceneElevation.RelativeToGround;
        string title = string.IsNullOrWhiteSpace(request.Title) ? $"{layer.Definition.Name} (3D)" : request.Title.Trim();
        byte[] package = SceneLayerPackageWriter.WritePoints(
            title, [.. carried.Select(f => new SceneField(f.Name, f.Type, f.Alias))], made, colour, 8, elevation, DateTimeOffset.UtcNow);

        return await KeepSceneAsync(context, service, index, title, $"{made.Count:N0} points", "points", made.Count, package, documents, audit, cancellation)
            .ConfigureAwait(false);
    }

    /// <summary>Keeps a generated package as a private document of the caller's and answers with where it is served.</summary>
    private static async Task<IResult> KeepSceneAsync(
        HttpContext context, PublishedService service, int index, string title, string counted, string noun, int count, byte[] package,
        IDocumentStore documents, IAuditLog audit, CancellationToken cancellation)
    {
        if (package.Length > MaximumDocumentBytes(context))
        {
            return Refuse(413, $"The scene layer came to {package.Length / (1 << 20)} MB, more than the {MaximumDocumentBytes(context) / (1 << 20)} MB a package may be here.");
        }

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;
        Document made = await documents.CreateAsync(
            title, $"{counted} from {service.QualifiedName}, layer {index}.", current.Principal.Id, SharingScope.Private,
            SafeFileName(title) + ".slpk", Documents.Named("slpk")!, package, cancellation).ConfigureAwait(false);

        await AuditAsync(
            context, audit, "document.create", made.Id,
            Detail(new { title = made.Title, kind = made.Kind.Name, bytes = made.Size, from = service.QualifiedName, layer = index, features = count, of = noun }),
            succeeded: true, cancellation).ConfigureAwait(false);

        context.Response.Headers.Location = $"/content/documents/{made.Id}";
        return Results.Json(
            new
            {
                id = made.Id, title = made.Title, features = count, of = noun,
                buildings = noun == "buildings" ? count : 0, bytes = made.Size, url = SceneServerEndpoints.AddressOf(context.Request, made.Id),
            },
            statusCode: StatusCodes.Status201Created);
    }

    private static bool Numeric(FieldType type) =>
        type is FieldType.SmallInteger or FieldType.Integer or FieldType.BigInteger or FieldType.Single or FieldType.Double;

    private static double Number(object? value) =>
        value is null or DBNull ? 0 : Convert.ToDouble(value, CultureInfo.InvariantCulture) is var d && double.IsFinite(d) ? d : 0;

    private static IReadOnlyList<IReadOnlyList<(double, double)>> Rings(Polygon polygon) =>
        [Ring(polygon.Shell), .. polygon.Holes.Select(Ring)];

    private static IReadOnlyList<(double, double)> Ring(LinearRing ring)
    {
        XySequence xy = ring.Coordinates;
        (double, double)[] points = new (double, double)[xy.Count];
        for (int i = 0; i < xy.Count; i++)
        {
            points[i] = (xy.X(i), xy.Y(i));
        }

        return points;
    }

    private static string SafeFileName(string title)
    {
        string name = new([.. title.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_')]);
        return name.Length == 0 ? "scene" : name[..Math.Min(name.Length, 80)];
    }
}
