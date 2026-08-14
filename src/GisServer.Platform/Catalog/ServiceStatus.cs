namespace GisServer.Platform.Catalog;

/// <summary>
/// Whether a service runs (ADR-020 §3).
/// </summary>
/// <remarks>
/// <para>
/// Separate from <c>SharingScope</c>, which answers a different question. This
/// one is operational: an operator takes a service out of rotation while its
/// source table is rebuilt, and puts it back without having to remember what its
/// sharing used to be.
/// </para>
/// <para>
/// <b>A stopped service answers 503, not 404</b>, to anyone permitted to know it
/// exists. It exists and is unavailable, which is a different sentence from
/// <em>no such layer</em>, and an operator restarting a client needs to see the
/// difference.
/// </para>
/// </remarks>
public enum ServiceStatus
{
    /// <summary>Serving.</summary>
    Started,

    /// <summary>Registered, deliberately unavailable.</summary>
    Stopped,
}
