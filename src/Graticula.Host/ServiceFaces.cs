using System.Linq;
using Graticula.Platform.Catalog;

namespace Graticula.Host;

/// <summary>
/// Which protocol faces a published service answers besides its FeatureServer.
/// </summary>
/// <remarks>
/// <b>One rule for everything that lists faces.</b> The services directory decided this inline, and a
/// copy of the tile rule that went stale is how a GeoParquet service's VectorTileServer answered and
/// was never listed (886e1ed). The portal's items are the second reader since 2026-09-15; both read here, and
/// OGC API Records is the third since 2026-10-06 (ADR-177).
/// </remarks>
internal static class ServiceFaces
{
    /// <summary>
    /// Whether the service has a MapServer: every service with a layer that has a geometry does (ADR-041).
    /// </summary>
    /// <param name="service">The service.</param>
    /// <returns>Whether it draws.</returns>
    public static bool Drawable(PublishedService service) =>
        service.Layers.Any(l => l.Definition.GeometryColumn is { Length: > 0 });

    /// <summary>
    /// Whether the service has a VectorTileServer: every layer in it can be tiled.
    /// </summary>
    /// <param name="service">The service.</param>
    /// <returns>Whether it tiles.</returns>
    public static bool Tileable(PublishedService service) =>
        service.Layers.Count > 0 && service.Layers.All(VectorTileEndpoints.Tileable);

    /// <summary>
    /// Whether WMS draws the service's layers: its feature face is on (D-123) and its owner has not turned WMS off
    /// (ADR-166).
    /// </summary>
    /// <remarks>
    /// <b>These three moved here on 2026-10-06 from the faces that asked them inline</b>, because OGC API Records
    /// (ADR-177) links a record to its service's OGC addresses and must link exactly the ones that answer. A copy of
    /// the rule in the catalogue would be the stale-copy failure this class exists to prevent. Running and sharing
    /// are not here: each face asks those of its caller, and so does the catalogue.
    /// </remarks>
    /// <param name="service">The service.</param>
    /// <returns>Whether its layers are in WMS.</returns>
    public static bool OffersWms(PublishedService service) =>
        service.Limits.AllowsFeatures(dataSupportsIt: true) && service.OffersOgc("WMS");

    /// <summary>Whether WFS serves the service's layers: its feature face is on and WFS is not turned off.</summary>
    /// <param name="service">The service.</param>
    /// <returns>Whether its layers are in WFS.</returns>
    public static bool OffersWfs(PublishedService service) =>
        service.Limits.AllowsFeatures(dataSupportsIt: true) && service.OffersOgc("WFS");

    /// <summary>
    /// Whether OGC API Features lists the service's layers: its feature face is on and the face is not turned off.
    /// </summary>
    /// <param name="service">The service.</param>
    /// <returns>Whether its layers are collections there.</returns>
    public static bool OffersOgcFeatures(PublishedService service) =>
        service.Limits.AllowsFeatures(dataSupportsIt: true) && service.OffersOgc("OGCFeatures");
}
