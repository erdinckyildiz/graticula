using System;

namespace Graticula.Platform.Catalog;

/// <summary>
/// Whether a vector tile service offers ArcGIS's <c>exportTiles</c> to its readers, and how much one export may
/// take — ADR-098 §5.5.
/// </summary>
/// <remarks>
/// <para>
/// <b>Off unless somebody turns it on, and that is the opposite of every other capability here.</b> The ADR-031
/// ceiling is null-means-unset: an unconfigured service offers what its data supports. An export is not something
/// the data supports or does not; it is a bulk copy of every tile in an area, written to the server's disk and
/// handed out as a file, and a service that existed before this was written was published by somebody who never
/// agreed to that. So the default is <see cref="Off"/>, and every existing service answers exactly as it did —
/// <c>exportTilesAllowed: false</c> — until its owner or an administrator says otherwise. ArcGIS makes the same
/// choice: <em>allow clients to export cache tiles</em> is an option a publisher enables.
/// </para>
/// <para>
/// <b>It is still a ceiling, not a grant.</b> Turning it on lets a caller who may already read the service take its
/// tiles away in one file; it never lets anybody read a service they could not, and the tile face turned off
/// (<see cref="ServiceCapabilityLimits.ServesTiles"/>) turns this off with it. <see cref="Anonymous"/> is the one
/// widening, and it is a separate switch for that reason: a public service whose map anybody may look at is not
/// thereby a service anybody may make the server package gigabytes for.
/// </para>
/// </remarks>
/// <param name="Allowed">Whether <c>exportTiles</c> is offered at all.</param>
/// <param name="Anonymous">Whether a caller who is not signed in may export, when the service is public.</param>
/// <param name="MaximumTiles">The most tiles one export of this service may hold, or null for the server's.</param>
public sealed record TileExportPolicy(bool Allowed, bool Anonymous, int? MaximumTiles)
{
    /// <summary>Not offered: every service's state until somebody turns it on.</summary>
    public static TileExportPolicy Off { get; } = new(false, false, null);

    /// <summary>The most tiles one export may hold on this service, given the server's own ceiling.</summary>
    /// <param name="serverMaximum">The server's ceiling — <c>Graticula:TileExportMaximumTiles</c>.</param>
    /// <returns>The lower of the two; a service can narrow the server's ceiling and never raise it.</returns>
    public long MaximumOf(long serverMaximum) =>
        MaximumTiles is { } mine ? Math.Min(mine, serverMaximum) : serverMaximum;

    /// <summary>Checks a policy an administrator sent.</summary>
    /// <returns>The sentence refusing it, or null.</returns>
    public string? Problem() =>
        MaximumTiles is < 1
            ? "The most tiles an export may hold is at least 1. Leave it empty for the server's own ceiling."
            : Anonymous && !Allowed
                ? "Anonymous export is a narrowing of export, so it needs export turned on as well."
                : null;
}
