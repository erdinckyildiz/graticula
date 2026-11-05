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
/// <param name="HeightField">The field holding each building's height in metres; none draws the footprints flat.</param>
/// <param name="BaseField">The field holding the height its floor stands at, or none for the ground (0).</param>
/// <param name="Title">The scene layer's title; the layer's otherwise.</param>
/// <param name="Colour">The buildings' colour as "#rrggbb"; a light grey otherwise.</param>
/// <param name="HeightScale">Metres per unit of the height field — 3 for a field of floors; 1 otherwise.</param>
internal sealed record SceneFromLayerRequest(
    string? Service, string? Folder, int? Layer, string? HeightField, string? BaseField, string? Title, string? Colour,
    double? HeightScale = null);

internal static partial class AdminEndpoints
{
    /// <summary>The most buildings one request turns into a scene layer — the rest wait for ADR-269's revisit.</summary>
    internal const int MaximumSceneBuildings = 200_000;

    private static void MapScenes(WebApplication app)
    {
        // ADR-269 §5.3: a polygon layer's footprints, extruded, written as a scene layer package the caller owns.
        app.MapPost("/content/scenes", CreateSceneFromLayerAsync);
    }

    /// <summary>
    /// Makes a 3D object scene layer from a polygon layer the caller can read, and keeps it as a package they own.
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

        if (layer.GeometryType is not (GeometryKind.Polygon or GeometryKind.MultiPolygon))
        {
            return Refuse(400, $"'{layer.Definition.Name}' is not a polygon layer. A scene layer of buildings is made from footprints.");
        }

        double scale = request.HeightScale ?? 1;
        if (!double.IsFinite(scale) || scale <= 0 || scale > 1000)
        {
            return Refuse(400, "heightScale is the metres one unit of the height field stands for: a number above 0, at most 1000.");
        }

        int[] colour = [220, 220, 215];
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

        // The fields carried into the scene: every one but the geometry and the object id.
        List<FieldDescription> carried = [.. described.Fields.Where(f =>
            !f.Name.Equals(layer.Definition.GeometryColumn, StringComparison.OrdinalIgnoreCase)
            && !f.Name.Equals(layer.Definition.IdentityColumn, StringComparison.OrdinalIgnoreCase)
            && f.Type is not (FieldType.Binary or FieldType.Unknown or FieldType.Boolean))];

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

        if (package.Length > MaximumDocumentBytes(context))
        {
            return Refuse(413, $"The scene layer came to {package.Length / (1 << 20)} MB, more than the {MaximumDocumentBytes(context) / (1 << 20)} MB a package may be here.");
        }

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;
        Document made = await documents.CreateAsync(
            title, $"{buildings.Count:N0} buildings from {service.QualifiedName}, layer {index}.", current.Principal.Id, SharingScope.Private,
            SafeFileName(title) + ".slpk", Documents.Named("slpk")!, package, cancellation).ConfigureAwait(false);

        await AuditAsync(
            context, audit, "document.create", made.Id,
            Detail(new { title = made.Title, kind = made.Kind.Name, bytes = made.Size, from = service.QualifiedName, layer = index, buildings = buildings.Count }),
            succeeded: true, cancellation).ConfigureAwait(false);

        context.Response.Headers.Location = $"/content/documents/{made.Id}";
        return Results.Json(
            new { id = made.Id, title = made.Title, buildings = buildings.Count, bytes = made.Size, url = SceneServerEndpoints.AddressOf(context.Request, made.Id) },
            statusCode: StatusCodes.Status201Created);

        static IResult Refuse(int status, string message) =>
            Results.Json(new { error = new { code = status, message, details = Array.Empty<string>() } }, statusCode: status);
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
