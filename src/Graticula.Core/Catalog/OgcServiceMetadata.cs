using System.Collections.Generic;

namespace Graticula.Catalog;

/// <summary>
/// What a service's own OGC capabilities document says about it — ADR-167: its description as the abstract, its tags
/// as keywords, and the fees and access constraints its owner states, as ArcGIS Manager's per-service OGC properties.
/// </summary>
/// <param name="Abstract">The service's description, or null for the server's own sentence.</param>
/// <param name="Keywords">The service's tags.</param>
/// <param name="Fees">What using it costs, or null for none stated.</param>
/// <param name="AccessConstraints">Who may use it and how, or null for none stated.</param>
public sealed record OgcServiceMetadata(
    string? Abstract, IReadOnlyList<string> Keywords, string? Fees, string? AccessConstraints)
{
    /// <summary>Its INSPIRE settings, which make its WMS a View service and its WFS a Download service (ADR-172).</summary>
    public InspireSettings? Inspire { get; init; }
}
