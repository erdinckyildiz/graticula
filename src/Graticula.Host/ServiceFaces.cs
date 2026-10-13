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

    /// <summary>Whether the service's FeatureServer answers: its feature face is not turned off (ADR-031).</summary>
    /// <remarks>
    /// <b>The three <c>Answers</c> rules are what a listing reads, and the two above are not enough on their own</b>
    /// (D-291). <see cref="Drawable"/> and <see cref="Tileable"/> say what the data could support; the doors
    /// also ask the owner's switches — <c>ServiceLookup.FeatureServiceAsync</c> and MapServer's
    /// <c>DrawableAsync</c> refuse with the feature face off, the tile routes with the tile face off. The services
    /// directory and the portal listed from the data alone, so a face turned off stayed listed and answered 404,
    /// and the listing told an anonymous caller what ADR-031 condition 2's identical refusal exists to hide.
    /// </remarks>
    /// <param name="service">The service.</param>
    /// <returns>Whether it answers as a FeatureServer.</returns>
    public static bool AnswersFeatures(PublishedService service) =>
        service.Limits.AllowsFeatures(dataSupportsIt: true);

    /// <summary>Whether the service's MapServer answers: it draws, and its feature face is on (ADR-041).</summary>
    /// <param name="service">The service.</param>
    /// <returns>Whether it answers as a MapServer.</returns>
    public static bool AnswersMap(PublishedService service) =>
        Drawable(service) && AnswersFeatures(service);

    /// <summary>Whether the service's VectorTileServer answers: it tiles, and its tile face is on.</summary>
    /// <param name="service">The service.</param>
    /// <returns>Whether it answers as a VectorTileServer.</returns>
    public static bool AnswersTiles(PublishedService service) =>
        Tileable(service) && service.Limits.AllowsTiles(dataSupportsIt: true);

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
        AnswersFeatures(service) && service.OffersOgc("WMS");

    /// <summary>Whether WFS serves the service's layers: its feature face is on and WFS is not turned off.</summary>
    /// <param name="service">The service.</param>
    /// <returns>Whether its layers are in WFS.</returns>
    public static bool OffersWfs(PublishedService service) =>
        AnswersFeatures(service) && service.OffersOgc("WFS");

    /// <summary>
    /// Whether OGC API Features lists the service's layers: its feature face is on and the face is not turned off.
    /// </summary>
    /// <param name="service">The service.</param>
    /// <returns>Whether its layers are collections there.</returns>
    public static bool OffersOgcFeatures(PublishedService service) =>
        AnswersFeatures(service) && service.OffersOgc("OGCFeatures");
}
