using System.Linq;
using Graticula.Platform.Catalog;

namespace Graticula.Host;

/// <summary>
/// Which protocol faces a published service answers besides its FeatureServer.
/// </summary>
/// <remarks>
/// <b>One rule for everything that lists faces.</b> The services directory decided this inline, and a
/// copy of the tile rule that went stale is how a GeoParquet service's VectorTileServer answered and
/// was never listed (886e1ed). The portal's items are the second reader since 2026-09-15; both read here.
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
}
