using System;
using Graticula.Platform.Identity;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>
/// Who a map area's packages are written for, and from where — ADR-288 §11a, D-404: the services, the caller's capabilities and
/// the server's own address, which is all the service and layer documents a package carries need.
/// </summary>
/// <remarks>
/// <para>
/// <b>A function of (service, caller), not of a request.</b> <see cref="MapAreaPackaging"/> takes this where it took an
/// <see cref="HttpContext"/>, so a refresh can run in the background (<see cref="MapAreaRefreshScheduler"/>) as the area's
/// creator, with the capabilities that creator has now.
/// </para>
/// <para>
/// <b>What is not extracted, and is said so (D-404).</b> The documents are still built by <c>Program.ServiceDocumentAsync</c>
/// and <c>LayerDocumentAsync</c>, whose helpers (<c>CapabilitiesFor</c>, <c>ViewFlags</c>, <c>SyncFlags</c>,
/// <c>ServiceItem</c>) read an <see cref="HttpContext"/> for exactly two things: its services and the caller's
/// <see cref="RequestPrincipal"/>. <see cref="Carrier"/> is that pair and an address in a context of its own, built from
/// nothing for a background run; it is not a request and carries no headers, query or body. Changing a dozen helpers that
/// every endpoint shares, to say the same two things in another type, was more risk than a refresh that must write the same
/// document a request does.
/// </para>
/// </remarks>
internal sealed class PackagingCaller
{
    private PackagingCaller(IServiceProvider services, RequestPrincipal principal, string origin, HttpContext carrier)
    {
        Services = services;
        Principal = principal;
        Origin = origin;
        Carrier = carrier;
    }

    /// <summary>The services.</summary>
    public IServiceProvider Services { get; }

    /// <summary>The caller, who owns the packages' replicas and whose capabilities the documents state.</summary>
    public RequestPrincipal Principal { get; }

    /// <summary>The server's address as a client reaches it, without a trailing slash.</summary>
    public string Origin { get; }

    /// <summary>What the document builders take: the services, the caller and the address.</summary>
    public HttpContext Carrier { get; }

    /// <summary>The caller a request is.</summary>
    /// <param name="context">The request.</param>
    /// <returns>The caller.</returns>
    public static PackagingCaller Of(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new PackagingCaller(context.RequestServices, context.Features.Get<RequestPrincipal>()!, TileFaces.Origin(context), context);
    }

    /// <summary>A caller with no request: a principal and the address the area was last reached at.</summary>
    /// <param name="services">The services (a scope's).</param>
    /// <param name="principal">The caller.</param>
    /// <param name="origin">The address, <c>scheme://host[:port][/base]</c>.</param>
    /// <returns>The caller.</returns>
    public static PackagingCaller Background(IServiceProvider services, RequestPrincipal principal, string origin)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);

        Uri address = new(origin, UriKind.Absolute);
        DefaultHttpContext carrier = new() { RequestServices = services };
        carrier.Features.Set(principal);
        carrier.Request.Scheme = address.Scheme;
        carrier.Request.Host = address.IsDefaultPort ? new HostString(address.Host) : new HostString(address.Host, address.Port);
        carrier.Request.PathBase = address.AbsolutePath.TrimEnd('/');

        return new PackagingCaller(services, principal, TileFaces.Origin(carrier), carrier);
    }
}
