namespace Graticula.Platform.Identity;

/// <summary>
/// What a session may be used for, beyond what its account may do — ADR-015 §4 mitigation 3, Q-154.
/// </summary>
/// <remarks>
/// <b>Null is every surface</b>, which is what the console's sign-in and every session before
/// migration 50 are. A scope narrows; it never grants.
/// </remarks>
public static class SessionScopes
{
    /// <summary>
    /// Issued by an ArcGIS token endpoint: the ArcGIS surfaces, and not the native administration
    /// API under <c>/admin</c>.
    /// </summary>
    public const string ArcGis = "arcgis";

    /// <summary>
    /// Whether a request path is the native administration API a scoped ArcGIS token does not open.
    /// </summary>
    /// <param name="path">The request path.</param>
    /// <returns>Whether it is under <c>/admin</c>, other than the token endpoint that issues such tokens.</returns>
    /// <remarks>
    /// <b><c>/rest/admin/services</c> is not this</b> — it is ArcGIS's own layer administration
    /// (addToDefinition, truncate), which ArcGIS clients reach with exactly these tokens.
    /// </remarks>
    public static bool IsNativeAdministration(string? path) =>
        path is not null
        && (path.Equals("/admin", System.StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/admin/", System.StringComparison.OrdinalIgnoreCase))
        && !path.Equals("/admin/generateToken", System.StringComparison.OrdinalIgnoreCase);
}
