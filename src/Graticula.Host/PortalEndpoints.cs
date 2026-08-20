using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Graticula.Host;

/// <summary>
/// The portal surface an ArcGIS client connects to, at <c>/sharing/rest</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists</b> — [ADR-040](../../docs/adr/ADR-040-the-portal-surface-is-how-arcgis-pro-connects.md).
/// ArcGIS Pro's *New ArcGIS Server* connection never reaches `/rest`: it probes
/// `/admin/generateToken` and then posts a SOAP body to `/services`, and stops
/// there. Its other connection type, *New Portal*, speaks the ArcGIS REST API
/// instead — which is JSON, is publicly documented, and is the road Esri's own
/// users are on. So the browse workflow is served from here rather than by
/// building a SOAP catalogue.
/// </para>
/// <para>
/// <b>An item is a published service and nothing is stored.</b> Its id is the
/// service's own id, its <c>url</c> is the FeatureServer or VectorTileServer
/// address that already answers, and its <c>access</c> is the sharing scope
/// [ADR-018](../../docs/adr/ADR-018-authorization-and-roles.md) already decides.
/// There is no second copy of the catalogue here, so there is nothing for the two
/// to disagree about — which is the property that makes this surface cheap to keep
/// and is the first thing that would be lost if an item ever held state of its own.
/// </para>
/// <para>
/// <b>The same filtering as everywhere else.</b> `VisibleAsync` evaluates sharing
/// through <see cref="LayerAccess"/> rather than reimplementing it, so an
/// anonymous caller's search returns what an anonymous caller may see and learns
/// nothing about the rest.
/// </para>
/// </remarks>
internal static class PortalEndpoints
{
    /// <summary>Where the surface lives.</summary>
    public const string Path = "/sharing/rest";

    /// <summary>
    /// The version this server reports to a portal client.
    /// </summary>
    /// <remarks>
    /// <b>It is a number a client compares against, not a description of us.</b>
    /// Pro decides which operations to attempt from it, so it names the portal API
    /// level this surface implements rather than the product's own version, which
    /// would mean nothing to the reader.
    /// </remarks>
    public const string PortalVersion = "11.2";

    /// <summary>Maps the surface.</summary>
    /// <param name="app">The application.</param>
    public static void MapPortal(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet(Path, InfoAsync).Governed(SharingGovernedExtensions.Public);
        app.MapGet($"{Path}/info", InfoAsync).Governed(SharingGovernedExtensions.Public);

        app.MapGet($"{Path}/generateToken", TokenAsync).Governed(SharingGovernedExtensions.Public);
        app.MapPost($"{Path}/generateToken", TokenAsync).Governed(SharingGovernedExtensions.Public);

        app.MapGet($"{Path}/portals/self", PortalSelfAsync)
            .Governed(SharingGovernedExtensions.ByFiltering);

        app.MapGet($"{Path}/community/self", CommunitySelfAsync)
            .Governed(SharingGovernedExtensions.ByFiltering);

        app.MapGet($"{Path}/search", SearchAsync)
            .Governed(SharingGovernedExtensions.ByFiltering);

        app.MapGet($"{Path}/content/items/{{id}}", ItemAsync)
            .Governed(SharingGovernedExtensions.ByFiltering);
    }

    /// <summary>
    /// Version and how to authenticate, which is where a portal client starts.
    /// </summary>
    /// <remarks>
    /// <b>The token URL it names must be one that answers.</b> `/rest/info` pointed
    /// at an endpoint speaking a different vocabulary for four days, and an ArcGIS
    /// client read that as *your password is wrong*. The same mistake is one line
    /// away here.
    /// </remarks>
    private static IResult InfoAsync(HttpContext context) => Results.Ok(new
    {
        currentVersion = PortalVersion,
        authInfo = new
        {
            isTokenBasedSecurity = true,
            tokenServicesUrl = $"{Origin(context)}{Path}/generateToken",
        },
    });

    /// <summary>
    /// The third spelling of one operation, and it shares the other two's lock.
    /// </summary>
    /// <remarks>
    /// <b>ADR-040 condition 3.</b> `/rest/generateToken`, `/admin/generateToken`
    /// and this one differ in error shape and in nothing else: one
    /// <see cref="LoginService"/>, one throttle, one audit record, one session
    /// store. A third door is where a copy usually appears.
    /// </remarks>
    private static async Task TokenAsync(
        HttpContext context, LoginService login, CancellationToken cancellation)
    {
        (string? name, string? password) = await CredentialsAsync(context, cancellation)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(password))
        {
            await PortalError(context, 400, "'username' and 'password' are required.")
                .ConfigureAwait(false);

            return;
        }

        LoginResult result = await login
            .AuthenticateAsync(name, password, context.Connection.RemoteIpAddress, cancellation)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            // One message for wrong-name, wrong-password and disabled: telling them
            // apart is an account-enumeration oracle. A throttle is still
            // distinguished, because a locked-out administrator cannot learn that
            // any other way.
            (int status, string message) = result.Failure switch
            {
                LoginFailure.AddressThrottled or LoginFailure.AccountThrottled => (
                    StatusCodes.Status429TooManyRequests,
                    "Too many failed sign-in attempts. Wait and try again."),
                _ => (
                    StatusCodes.Status401Unauthorized,
                    "Unable to generate token. The name or password is incorrect."),
            };

            await PortalError(context, status, message).ConfigureAwait(false);
            return;
        }

        AuthenticatedSession session = result.Session!.Value;

        await Results.Json(new
        {
            token = result.Token!,
            expires = session.ExpiresAt.ToUnixTimeMilliseconds(),
            ssl = true,
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>The organisation, as this caller sees it.</summary>
    /// <remarks>
    /// <b>The identity is the server's, not a per-request accident.</b> A portal's
    /// id is a thing clients cache and compare, so it is derived from the origin
    /// rather than generated — two requests to the same deployment must describe
    /// the same portal or a client concludes it has moved.
    /// </remarks>
    private static IResult PortalSelfAsync(HttpContext context)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        bool signedIn = current.Principal != Principal.Anonymous;

        return Results.Ok(new
        {
            id = PortalId(context),
            name = "Graticula",
            portalName = "Graticula",
            portalHostname = context.Request.Host.Value,
            isPortal = true,
            allSSL = true,
            supportsHostedServices = true,
            currentVersion = PortalVersion,
            access = "private",
            user = signedIn ? Self(current) : null,

            // <b>Pro asks where the geometry service is rather than assuming.</b>
            // We have one (ADR-022) and it is at the address every ArcGIS client
            // looks for, so naming it here is free.
            helperServices = new
            {
                geometry = new
                {
                    url = $"{Origin(context)}/rest/services/Utilities/Geometry/GeometryServer",
                },
            },
        });
    }

    private static IResult CommunitySelfAsync(HttpContext context)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (current.Principal == Principal.Anonymous)
        {
            // A portal answers this with an error rather than an empty user, and a
            // client uses it to decide whether its token is still good.
            return Results.Json(
                new { error = new { code = 499, message = "Token Required", details = Array.Empty<string>() } },
                statusCode: StatusCodes.Status499ClientClosedRequest);
        }

        return Results.Ok(Self(current));
    }

    private static object Self(RequestPrincipal current) => new
    {
        username = current.Principal.Name,
        fullName = current.Principal.Name,
        access = "private",

        // <b>Role is reported as what this caller can do, not as a stored value.</b>
        // ADR-035 made privileges editable, so a fixed role string would be a claim
        // that goes stale the first time somebody edits one.
        role = current.Authorization.Allows(Privilege.AdminManageServer)
            ? "org_admin"
            : "org_user",
    };

    /// <summary>Published services, as portal items.</summary>
    private static async Task<IResult> SearchAsync(
        HttpContext context,
        PostgresLayerCatalog catalog,
        CancellationToken cancellation)
    {
        IReadOnlyList<PublishedService> visible =
            await VisibleAsync(context, catalog, cancellation).ConfigureAwait(false);

        List<object> results = [];

        foreach (PublishedService service in visible)
        {
            results.Add(Item(context, service));
        }

        return Results.Ok(new
        {
            query = context.Request.Query["q"].ToString(),
            total = results.Count,
            start = 1,
            num = results.Count,

            // -1 means there is no next page, which is true: this returns every
            // item the caller may see. Paging arrives when a deployment has enough
            // items for it to matter, and not before (§82).
            nextStart = -1,
            results,
        });
    }

    private static async Task<IResult> ItemAsync(
        HttpContext context,
        PostgresLayerCatalog catalog,
        string id,
        CancellationToken cancellation)
    {
        IReadOnlyList<PublishedService> visible =
            await VisibleAsync(context, catalog, cancellation).ConfigureAwait(false);

        foreach (PublishedService service in visible)
        {
            if (string.Equals(ItemId(service), id, StringComparison.OrdinalIgnoreCase))
            {
                return Results.Ok(Item(context, service));
            }
        }

        // <b>The same answer whether it does not exist or is not visible.</b> A
        // caller who may not see an item must not be able to tell the two apart,
        // which is the rule every other surface here applies.
        return Results.Json(
            new
            {
                error = new
                {
                    code = 400,
                    message = "Item does not exist or is inaccessible.",
                    details = Array.Empty<string>(),
                },
            },
            statusCode: StatusCodes.Status400BadRequest);
    }

    /// <summary>Every service this caller may see, running.</summary>
    private static async Task<IReadOnlyList<PublishedService>> VisibleAsync(
        HttpContext context, PostgresLayerCatalog catalog, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        bool seesStopped = current.Authorization.Allows(Privilege.AdminManageServer);

        IReadOnlyList<PublishedService> services =
            await catalog.ListServicesAsync(cancellation).ConfigureAwait(false);

        return
        [
            .. services.Where(service =>
                (service.IsRunning || seesStopped)
                && LayerAccess
                    .Evaluate(
                        service.Sharing, service.Owner, current.Principal, current.Authorization,
                        service.SharedWith)
                    .IsAllowed()),
        ];
    }

    private static object Item(HttpContext context, PublishedService service)
    {
        bool tiles = string.Equals(service.Kind, "VectorTileServer", StringComparison.OrdinalIgnoreCase);

        string face = tiles ? "VectorTileServer" : "FeatureServer";

        return new
        {
            id = ItemId(service),
            owner = "graticula",
            title = service.Name,
            name = service.Name,
            type = tiles ? "Vector Tile Service" : "Feature Service",

            // <b>Pro reads these to decide what an item is before it opens it.</b>
            // An item with no type keywords is one it will not offer to add.
            typeKeywords = tiles
                ? new[] { "ArcGIS Server", "Data", "Service", "Vector Tile Service", "Hosted Service" }
                : new[] { "ArcGIS Server", "Data", "Feature Access", "Feature Service", "Service", "Hosted Service" },
            description = service.Description,
            snippet = service.Description,
            tags = service.Folder is null ? Array.Empty<string>() : new[] { service.Folder },
            url = $"{Origin(context)}/rest/services/{service.QualifiedName}/{face}",
            access = Access(service.Sharing),
            spatialReference = (string?)null,
            numViews = 0,
            size = -1,
        };
    }

    /// <summary>
    /// The service's own id, spelled the way portal items are.
    /// </summary>
    /// <remarks>
    /// <b>No new identifier is minted.</b> An Esri item id is 32 hexadecimal
    /// characters and a GUID in "N" format is exactly that, so the service's own id
    /// is the item's id — which means an item cannot come to refer to a service
    /// that has been republished, because there is nothing to keep in step.
    /// </remarks>
    private static string ItemId(PublishedService service) => service.Id.ToString("N");

    /// <summary>
    /// A sharing scope, in the word a portal client uses for it.
    /// </summary>
    /// <remarks>
    /// <b>Every scope is named and there is no catch-all</b>, which the
    /// architecture suite insisted on and was right to: a `_ => "private"` default
    /// turns a fifth scope into a silent downgrade, and D-74 is what happens when a
    /// value is added to an enumeration and its readers are not. A scope this does
    /// not know is a build-time surprise here rather than a run-time lie to a
    /// client.
    ///
    /// The mapping itself: <c>private</c> stays private, <c>organization</c> is
    /// <c>org</c>, <c>public</c> is <c>public</c>, and a <c>group</c> share is
    /// <c>shared</c> — which is the nearest word portal has for *visible to some
    /// people and not everyone*.
    /// </remarks>
    private static string Access(SharingScope sharing) => sharing switch
    {
        SharingScope.Private => "private",
        SharingScope.Organization => "org",
        SharingScope.Public => "public",
        SharingScope.Group => "shared",
        _ => throw new ArgumentOutOfRangeException(
            nameof(sharing),
            sharing,
            "This sharing scope has no portal access level. Adding a scope means deciding what a "
            + "portal client should be told about it, not defaulting it to private."),
    };

    private static async Task<(string? Name, string? Password)> CredentialsAsync(
        HttpContext context, CancellationToken cancellation)
    {
        string? name = null;
        string? password = null;

        if (context.Request.HasFormContentType)
        {
            IFormCollection form = await context.Request.ReadFormAsync(cancellation)
                .ConfigureAwait(false);

            name = form["username"].ToString();
            password = form["password"].ToString();
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            name = context.Request.Query["username"].ToString();
        }

        if (string.IsNullOrEmpty(password))
        {
            password = context.Request.Query["password"].ToString();
        }

        return (name, password);
    }

    private static Task PortalError(HttpContext context, int status, string message) =>
        Results.Json(
            new
            {
                error = new
                {
                    code = status,
                    message,
                    details = Array.Empty<string>(),
                },
            },
            statusCode: status).ExecuteAsync(context);

    private static string Origin(HttpContext context) =>
        $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}";

    /// <summary>
    /// A portal id that is the same for every request to one deployment.
    /// </summary>
    /// <remarks>
    /// Derived from the origin rather than generated, because a client caches it
    /// and a portal whose id changes is a portal that has been replaced.
    /// </remarks>
    private static string PortalId(HttpContext context)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Origin(context)));

        return Convert.ToHexString(hash)[..32].ToLowerInvariant();
    }
}
