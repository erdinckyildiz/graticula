using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace GisServer.Host;

/// <summary>
/// Marks a route as having a sharing decision behind it, and names which.
/// </summary>
/// <param name="Source">What governs it, for the audit listing.</param>
/// <remarks>
/// <para>
/// <b>This exists because a whole service shipped ungoverned and nothing
/// noticed.</b> The geometry service answered anonymously from the day it was
/// written — not by anyone's decision, but because sharing was a property of a
/// <em>layer</em> and that service has none
/// ([ADR-018](../../docs/adr/ADR-018-authorization-and-roles.md) §3b-i). The
/// sharing code was correct throughout; what was missing was a place for
/// something that is not content, and an absence has nothing for a reviewer to
/// look at.
/// </para>
/// <para>
/// <b>A marker rather than a mechanism, deliberately.</b> It enforces nothing at
/// run time — the enforcement is <see cref="ServiceLookup"/> and the geometry
/// group's filter. What it does is make the property <em>enumerable</em>: every
/// route under <c>/rest/services</c> either carries one of these or is listed by
/// <c>/admin/routes</c> as ungoverned, and a test fails when that list is not
/// empty. Applying the marker without the check would be a lie that is one
/// keystroke away, which is why the test asserts behaviour the marker only
/// describes.
/// </para>
/// </remarks>
public sealed record SharingGoverned(string Source);

/// <summary>Applies <see cref="SharingGoverned"/> to route builders.</summary>
internal static class SharingGovernedExtensions
{
    /// <summary>A layer or service's own sharing scope, via ServiceLookup.</summary>
    public const string ByService = "the service's sharing scope";

    /// <summary>A system_service row, via the geometry group's filter.</summary>
    public const string BySystemService = "the system service's sharing scope";

    /// <summary>The catalogue, which filters rather than refuses.</summary>
    public const string ByFiltering = "listed only what the caller may see";

    /// <summary>Deliberately open to everyone, with the reason recorded.</summary>
    public const string Public = "public by design";

    /// <summary>Records what governs this route.</summary>
    /// <typeparam name="T">The builder type.</typeparam>
    /// <param name="builder">The route or group.</param>
    /// <param name="source">One of the constants on this class.</param>
    /// <returns>The builder.</returns>
    public static T Governed<T>(this T builder, string source)
        where T : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        builder.WithMetadata(new SharingGoverned(source));
        return builder;
    }
}
