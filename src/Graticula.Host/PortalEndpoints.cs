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

        // <b>GET and HEAD together, because Pro leads with HEAD.</b> It sends
        // HEAD before every probe here and reads a 405 as a dead end — measured:
        // `HEAD /sharing/rest/portals/self` answered 405, the GET after it answered
        // 200, and the connection still failed. HTTP says a resource answering GET
        // answers HEAD, and [D-121](../../docs/architecture-debt.md) records that
        // the rest of this server still does not.
        Discoverable(app, Path, InfoAsync).Governed(SharingGovernedExtensions.Public);
        Discoverable(app, $"{Path}/info", InfoAsync).Governed(SharingGovernedExtensions.Public);

        // <b>The file Pro needs before it will believe a URL is a portal.</b>
        // Measured three times: without it Pro tries `/arcgisuris.xml/sharing/rest`
        // and gives up, with `<Name>Graticula</Name>` it decides this is ArcGIS
        // Online and leaves for arcgis.com, and the working Enterprise portals this
        // was compared against answer `<Name>Portal for ArcGIS</Name>`. The name is
        // a token a client matches on rather than anything shown to a person — the
        // same category as the `currentVersion` `/rest/info` already reports.
        Discoverable(app, "/arcgisuris.xml", UriListAsync)
            .Governed(SharingGovernedExtensions.Public);

        // <b>Deleted by an edit and caught by a test, which is the point of the
        // test.</b> Rewriting the block above took these two with it, and the
        // surface still answered every discovery request — a client would have got
        // all the way to signing in before finding out. The conformance suite found
        // it in the same run.
        app.MapGet($"{Path}/generateToken", TokenAsync)
            .Governed(SharingGovernedExtensions.Public);

        app.MapPost($"{Path}/generateToken", TokenAsync)
            .Governed(SharingGovernedExtensions.Public);

        Discoverable(app, $"{Path}/portals/self", PortalSelfAsync)
            .Governed(SharingGovernedExtensions.ByFiltering);

        // <b>The organisation by id, under both of its names.</b> Pro asks for
        // `accounts/{id}` — the older spelling — right after it has read the user's
        // profile, and answered 404 it reports the sign-in as a failed connection.
        // `portals/{id}` is the same document under the current name, and a client
        // that used one and not the other would find half a portal.
        //
        // <b>The id has to be the one this server just gave out.</b> Pro takes it
        // from `portals/self` and asks for it back; anything else is a portal
        // describing an organisation it does not have.
        Discoverable(app, $"{Path}/accounts/{{id}}", OrganizationAsync)
            .Governed(SharingGovernedExtensions.ByFiltering);

        Discoverable(app, $"{Path}/portals/{{id}}", OrganizationAsync)
            .Governed(SharingGovernedExtensions.ByFiltering);

        Discoverable(app, $"{Path}/community/self", CommunitySelfAsync)
            .Governed(SharingGovernedExtensions.ByFiltering);

        // <b>What Pro asks for immediately after signing in.</b> It generated a
        // token, then fetched this, got 404 and reported *unable to connect* — a
        // sign-in that had already succeeded, failing on the profile behind it.
        Discoverable(app, $"{Path}/community/users/{{username}}", UserAsync)
            .Governed(SharingGovernedExtensions.ByFiltering);

        // <b>POST as well as GET, because Pro posts its searches.</b> Its query is
        // long enough to be a paragraph — thirty negated type clauses — so it uses
        // a body, and a GET-only route answers 405 to the one request that lists
        // anybody's content.
        app.MapMethods($"{Path}/search", ["GET", "HEAD", "POST"], SearchAsync)
            .Governed(SharingGovernedExtensions.ByFiltering);

        // The signed-in user's own content, which is the first thing Pro opens.
        Discoverable(app, $"{Path}/content/users/{{username}}", UserContentAsync)
            .Governed(SharingGovernedExtensions.ByFiltering);

        // <b>Three documents an organisation has and this one does not.</b> A
        // subscription it is not sold under, a category schema nobody has defined,
        // and a group list this surface does not publish yet. Each answers with an
        // empty truth rather than a 404, because Pro asks four times and reads the
        // absence as a broken portal.
        Discoverable(app, $"{Path}/portals/{{id}}/subscriptionInfo", SubscriptionAsync)
            .Governed(SharingGovernedExtensions.ByFiltering);

        Discoverable(app, $"{Path}/portals/{{id}}/categorySchema", CategorySchemaAsync)
            .Governed(SharingGovernedExtensions.ByFiltering);

        app.MapMethods($"{Path}/community/groups", ["GET", "HEAD", "POST"], GroupsAsync)
            .Governed(SharingGovernedExtensions.ByFiltering);

        Discoverable(app, $"{Path}/content/items/{{id}}", ItemAsync)
            .Governed(SharingGovernedExtensions.ByFiltering);

        // <b>The item's own data, of which these items have none.</b> Pro asks for
        // it as the last step of adding a layer to a map — after it has already
        // read the FeatureServer document successfully — and a 404 there stops the
        // add. A portal item that is a pointer to a service carries no data
        // document; the service is the data. So this answers *nothing*, which is
        // true, rather than *no such thing*, which is not.
        Discoverable(app, $"{Path}/content/items/{{id}}/data", ItemDataAsync)
            .Governed(SharingGovernedExtensions.ByFiltering);
    }

    /// <summary>
    /// What an administrator may do, in the words a portal client reads.
    /// </summary>
    /// <remarks>
    /// <b>What this account may do, and nothing it may not.</b> A client reads
    /// these to decide which buttons to offer, so a generous list is a set of
    /// actions that fail when somebody presses them — the same failure as
    /// advertising an operator the filter reader refuses.
    /// </remarks>
    private static readonly string[] AdministratorPrivileges =
    [
        "portal:user:viewOrgItems",
        "portal:user:viewOrgUsers",
        "portal:admin:viewItems",
    ];

    /// <summary>What everybody else may do.</summary>
    private static readonly string[] MemberPrivileges = ["portal:user:viewOrgItems"];

    /// <summary>Maps a route that answers HEAD as well as GET.</summary>
    private static RouteHandlerBuilder Discoverable(
        WebApplication app, string pattern, Delegate handler) =>
        app.MapMethods(pattern, ["GET", "HEAD"], handler);

    /// <summary>
    /// Where the portal is, for a client given only a host.
    /// </summary>
    /// <remarks>
    /// <b>Only the fields that point somewhere real.</b> A working portal's file
    /// also carries a basemap query, a Bing adaptor on Esri's own servers and a
    /// speed-test download; none of those exist here, and a client following a link
    /// to nothing is worse than a client finding a shorter list.
    /// </remarks>
    private static IResult UriListAsync(HttpContext context)
    {
        string origin = Origin(context) + "/";

        string xml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
            + "<ArcGISOnlineURIList>"
            + "<Name>Portal for ArcGIS</Name>"
            + $"<Base>{origin}</Base>"
            + $"<Secure>{origin}</Secure>"
            + $"<Update>{origin}updates/</Update>"
            + $"<PingTest>{origin}</PingTest>"
            + $"<NewAccount>{origin}rest/login</NewAccount>"
            + "<ForgottenPassword></ForgottenPassword>"
            + "</ArcGISOnlineURIList>";

        return Results.Content(xml, "text/xml; charset=utf-8");
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
            // <b>Sixteen characters, because that is what a portal's id is.</b>
            // An item id is thirty-two and a portal id is not, and a client that
            // measures the difference concluded this was something else.
            id = PortalId(context),

            // <b>`name` is ours and `portalName` is the product's.</b> A working
            // Enterprise portal answers "ArcGIS Enterprise" here and gives its own
            // name in `name`; matching that is how a client decides which sign-in
            // flow to use. Saying "Graticula" in both sent Pro to arcgis.com.
            name = "Graticula",
            portalName = "ArcGIS Enterprise",
            portalMode = "singletenant",
            customBaseUrl = string.Empty,
            portalHostname = context.Request.Host.Value,
            isPortal = true,
            allSSL = true,

            // <b>False, and it has to stay false until it is true.</b> This server
            // has no OAuth. Claiming it would repeat exactly the mistake that cost
            // three attempts: a client believes a capability that is advertised,
            // goes to use it, and never comes back.
            supportsOAuth = false,
            supportsHostedServices = true,
            httpPort = 80,
            httpsPort = 443,
            currentVersion = PortalVersion,
            access = "public",
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

    /// <summary>The organisation named by id, which is the one this server is.</summary>
    /// <remarks>
    /// <b>The same document as <c>portals/self</c>, deliberately.</b> A second
    /// description of one organisation is a second thing to keep in step, and this
    /// server has exactly one — so the id is checked and the answer is the same
    /// document, rather than a copy assembled beside it.
    /// </remarks>
    private static IResult OrganizationAsync(HttpContext context, string id)
    {
        if (!string.Equals(id, PortalId(context), StringComparison.OrdinalIgnoreCase))
        {
            return Results.Json(
                new
                {
                    error = new
                    {
                        code = 400,
                        message = "Organization does not exist or is inaccessible.",
                        details = Array.Empty<string>(),
                    },
                },
                statusCode: StatusCodes.Status400BadRequest);
        }

        return PortalSelfAsync(context);
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

    /// <summary>One user's profile.</summary>
    /// <remarks>
    /// <b>Only the caller's own, and that is a decision rather than a shortcut.</b>
    /// A portal lets members look each other up; this server has no such surface
    /// and inventing one here would publish the member list through a door nobody
    /// reviewed. Asking about somebody else gets the same answer as asking about a
    /// name that does not exist, which is the rule every other surface follows.
    /// </remarks>
    private static IResult UserAsync(HttpContext context, string username)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (current.Principal == Principal.Anonymous
            || !string.Equals(current.Principal.Name, username, StringComparison.OrdinalIgnoreCase))
        {
            return Results.Json(
                new
                {
                    error = new
                    {
                        code = 400,
                        message = "User does not exist or is inaccessible.",
                        details = Array.Empty<string>(),
                    },
                },
                statusCode: StatusCodes.Status400BadRequest);
        }

        bool administrator = current.Authorization.Allows(Privilege.AdminManageServer);

        return Results.Ok(new
        {
            username = current.Principal.Name,
            fullName = current.Principal.Name,
            firstName = current.Principal.Name,
            lastName = string.Empty,
            description = (string?)null,
            email = (string?)null,
            orgId = PortalId(context),
            role = administrator ? "org_admin" : "org_user",
            roleId = administrator ? "org_admin" : "org_user",

            // <b>What this account may do, and nothing it may not.</b> A client
            // reads these to decide which buttons to offer, so a generous list is a
            // set of actions that fail when somebody presses them — the same
            // failure as advertising an operator the filter reader refuses.
            privileges = administrator ? AdministratorPrivileges : MemberPrivileges,
            access = "private",
            provider = "arcgis",
            userType = "creatorUT",
            level = "2",
            disabled = false,
            units = "metric",

            // <b>Empty, and it is not a claim that this account has no groups.</b>
            // ADR-036's groups exist and are not published through this surface
            // yet; naming that here rather than in a register would hide it, so it
            // is in both.
            groups = Array.Empty<object>(),
        });
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

    /// <summary>
    /// A portal's subscription, of which this server has none.
    /// </summary>
    /// <remarks>
    /// <b>An empty truth rather than a 404.</b> This product is given away
    /// (Q-73), so there is no subscription to describe — but a client that gets no
    /// answer at all concludes the portal is broken, and it asks four times before
    /// deciding. Saying *in house, active, no expiry* is what a portal nobody
    /// invoices looks like.
    /// </remarks>
    private static IResult SubscriptionAsync(HttpContext context, string id) =>
        string.Equals(id, PortalId(context), StringComparison.OrdinalIgnoreCase)
            ? Results.Ok(new
            {
                id = PortalId(context),
                type = "In House",
                state = "active",
                expDate = -1,
                maxUsersPerLevel = new { },
            })
            : Unknown("Organization");

    /// <summary>The item categories an organisation has defined, which is none.</summary>
    private static IResult CategorySchemaAsync(HttpContext context, string id) =>
        string.Equals(id, PortalId(context), StringComparison.OrdinalIgnoreCase)
            ? Results.Ok(new { categorySchema = Array.Empty<object>() })
            : Unknown("Organization");

    /// <summary>
    /// Groups, which this surface does not publish yet.
    /// </summary>
    /// <remarks>
    /// <b>Empty is honest here and would not be for long.</b>
    /// [ADR-036](../../docs/adr/ADR-036-groups.md)'s groups exist and are real; what
    /// does not exist is a decision about how they map onto portal groups, which
    /// carry their own membership and sharing semantics. Answering with an empty
    /// list says *this portal has no groups*, which is wrong, and answering 404
    /// says *this is not a portal*, which is worse. Recorded rather than resolved:
    /// [Q-127](../../docs/open-questions.md).
    /// </remarks>
    private static IResult GroupsAsync() => Results.Ok(new
    {
        total = 0,
        start = 1,
        num = 0,
        nextStart = -1,
        results = Array.Empty<object>(),
    });

    /// <summary>One user's content, which is every item they may see.</summary>
    /// <remarks>
    /// <b>Not *items they own*, and the difference is recorded rather than
    /// hidden.</b> A portal's content listing is per-owner and this server's items
    /// are published services whose owner is the account that published them, not
    /// the portal. Until ownership is carried through
    /// ([Q-127](../../docs/open-questions.md)) this answers with what the caller may
    /// see, which is a larger set than Pro's *My Content* implies.
    /// </remarks>
    private static async Task<IResult> UserContentAsync(
        HttpContext context,
        PostgresLayerCatalog catalog,
        string username,
        CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (current.Principal == Principal.Anonymous
            || !string.Equals(current.Principal.Name, username, StringComparison.OrdinalIgnoreCase))
        {
            return Unknown("User");
        }

        IReadOnlyList<PublishedService> visible =
            await VisibleAsync(context, catalog, cancellation).ConfigureAwait(false);

        List<object> items = [.. visible.Select(service => Item(context, service))];

        return Results.Ok(new
        {
            username,
            total = items.Count,
            start = 1,
            num = items.Count,
            nextStart = -1,
            currentFolder = (object?)null,
            items,
            folders = Array.Empty<object>(),
        });
    }

    private static IResult Unknown(string what) => Results.Json(
        new
        {
            error = new
            {
                code = 400,
                message = what + " does not exist or is inaccessible.",
                details = Array.Empty<string>(),
            },
        },
        statusCode: StatusCodes.Status400BadRequest);

    /// <summary>Published services, as portal items.</summary>
    private static async Task<IResult> SearchAsync(
        HttpContext context,
        PostgresLayerCatalog catalog,
        CancellationToken cancellation)
    {
        IReadOnlyList<PublishedService> visible =
            await VisibleAsync(context, catalog, cancellation).ConfigureAwait(false);

        string query = context.Request.Query["q"].ToString();

        if (query.Length == 0 && context.Request.HasFormContentType)
        {
            IFormCollection form = await context.Request.ReadFormAsync(cancellation)
                .ConfigureAwait(false);

            query = form["q"].ToString();
        }

        List<object> results = [];

        foreach (PublishedService service in visible)
        {
            object item = Item(context, service);

            if (PortalQuery.Matches(item, query))
            {
                results.Add(item);
            }
        }

        return Results.Ok(new
        {
            query,
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

    /// <summary>
    /// An item's data document, which for a service pointer is empty.
    /// </summary>
    /// <remarks>
    /// <b>The visibility check is the same one as for the item itself.</b> An
    /// empty answer is still an answer, and an empty answer about an item this
    /// caller may not see would tell them it exists.
    /// </remarks>
    private static async Task<IResult> ItemDataAsync(
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
                return Results.Ok(new { });
            }
        }

        return Unknown("Item");
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
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        // <b>The caller's name when the caller owns it, and the product's when
        // nobody does.</b> Pro's *My Content* asks for `owner:<username>`, so an
        // item whose owner is a constant is an item that never appears there.
        // A service owned by somebody else is still not attributed to them: this
        // surface has no member directory, and inventing one to fill a field would
        // publish the user list through a door nobody reviewed.
        string owner = service.Owner is { } id && id == current.Principal.Id
            ? current.Principal.Name
            : "graticula";

        bool tiles = string.Equals(service.Kind, "VectorTileServer", StringComparison.OrdinalIgnoreCase);

        string face = tiles ? "VectorTileServer" : "FeatureServer";

        return new
        {
            id = ItemId(service),
            owner,
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

        return Convert.ToHexString(hash)[..16].ToUpperInvariant();
    }
}
