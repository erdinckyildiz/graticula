using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Text.Json;
using System.Threading.Tasks;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
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
/// <b>Since ADR-079 one kind of item does: a saved web map.</b> It is stored in its own
/// table, authoritative for its own owner and scope, and listed here beside the services
/// under the same <see cref="LayerAccess"/> rule — a second source for this listing, which
/// ADR-079 condition 3 says the next item kind must not become a third of.
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

        // <b>And its folders — ADR-114.</b> The folder's id is 32 hexadecimal digits, as Portal's are, which also
        // keeps this route clear of the operations below.
        Discoverable(app, $"{Path}/content/users/{{username}}/{{folderId:length(32)}}", UserFolderContentAsync)
            .Governed(SharingGovernedExtensions.ByFiltering);
        app.MapPost($"{Path}/content/users/{{username}}/createFolder", CreateFolderAsync)
            .Governed(SharingGovernedExtensions.ByOwnership).DisableAntiforgery();
        app.MapPost($"{Path}/content/users/{{username}}/{{folderId:length(32)}}/delete", DeleteFolderAsync)
            .Governed(SharingGovernedExtensions.ByOwnership).DisableAntiforgery();
        app.MapPost($"{Path}/content/users/{{username}}/items/{{id}}/move", MoveItemAsync)
            .Governed(SharingGovernedExtensions.ByOwnership).DisableAntiforgery();
        app.MapPost($"{Path}/content/users/{{username}}/moveItems", MoveItemsAsync)
            .Governed(SharingGovernedExtensions.ByOwnership).DisableAntiforgery();

        // <b>Two documents an organisation has and this one does not.</b> A
        // subscription it is not sold under and a category schema nobody has
        // defined. Each answers with an empty truth rather than a 404, because Pro
        // asks four times and reads the absence as a broken portal. There were
        // three until 2026-09-09, and the third was not an absence — see
        // <see cref="GroupsAsync"/>.
        Discoverable(app, $"{Path}/portals/{{id}}/subscriptionInfo", SubscriptionAsync)
            .Governed(SharingGovernedExtensions.ByFiltering);

        Discoverable(app, $"{Path}/portals/{{id}}/categorySchema", CategorySchemaAsync)
            .Governed(SharingGovernedExtensions.ByFiltering);

        // POST as well as GET, because a portal search is a search: Pro sends `q`
        // in a form body when it is long enough to be worth one.
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

        // <b>The item's picture — V-50, the third ArcGIS review.</b> The server draws every layer's
        // thumbnail and keeps it (ADR-071), and the console shows them; a portal client did not, because
        // the item named no `thumbnail` and this address did not exist, so Pro's catalogue and every
        // Online-style gallery showed the grey placeholder for every service. The file name is whatever
        // the item says; the route answers for the one it says and 404s the rest.
        Discoverable(app, $"{Path}/content/items/{{id}}/info/thumbnail/{{file}}", ItemThumbnailAsync)
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
        // The exchange a federated server's client asks for — AuthEndpoints.TryExchangeAsync.
        if (await AuthEndpoints.TryExchangeAsync(context, cancellation).ConfigureAwait(false) is { Asked: true } exchanged)
        {
            if (exchanged.Error is { } refusal)
            {
                await PortalError(context, exchanged.Status, refusal).ConfigureAwait(false);
                return;
            }

            await Results.Json(new
            {
                token = exchanged.Token,
                expires = exchanged.Expires.ToUnixTimeMilliseconds(),
                ssl = context.RequestServices.GetRequiredService<HostSettings>().RequireHttps,
            }).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        (string? name, string? password) = await CredentialsAsync(context, cancellation)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(password))
        {
            await PortalError(context, 400, "'username' and 'password' are required.")
                .ConfigureAwait(false);

            return;
        }

        // <b>`CallerAddress`, as the other two doors use — D-12.</b> This read the socket address, so
        // behind a proxy every portal sign-in was throttled as the proxy's.
        (bool bindable, string? bound, string? unbindable) =
            await AuthEndpoints.RequestedBindingAsync(context, cancellation).ConfigureAwait(false);

        if (!bindable)
        {
            await PortalError(context, 400, unbindable!).ConfigureAwait(false);
            return;
        }

        LoginResult result = await login
            .AuthenticateAsync(
                name, password, CallerAddress.Of(context), cancellation,
                await AuthEndpoints.RequestedLifetimeAsync(context, cancellation).ConfigureAwait(false),
                bound, SessionScopes.ArcGis)
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
            ssl = context.RequestServices.GetRequiredService<HostSettings>().RequireHttps,
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>The operator's map ground as a web map basemap, or OpenStreetMap when there is none.</summary>
    /// <remarks>
    /// <b>Only the services this caller may draw.</b> A ground shared with a group is that group's ground;
    /// naming it to everybody else would send their map to a 404 for every tile and draw nothing, which is
    /// worse than the OpenStreetMap they get instead. When none is left, the answer is OpenStreetMap —
    /// Q-110's default, spelled the way a web map spells it.
    /// </remarks>
    private static async Task<object> DefaultBasemapAsync(
        HttpContext context, RequestPrincipal current, CancellationToken cancellation)
    {
        ServerGround.Reading ground = await context.RequestServices
            .GetRequiredService<ServerGround>()
            .ReadAsync(cancellation)
            .ConfigureAwait(false);

        Graticula.Platform.Postgres.CatalogFallback catalog =
            context.RequestServices.GetRequiredService<Graticula.Platform.Postgres.CatalogFallback>();

        List<object> layers = [];

        foreach (string qualified in ground.Services)
        {
            int slash = qualified.IndexOf('/', StringComparison.Ordinal);

            Graticula.Platform.Postgres.CatalogAnswer answer = await catalog
                .FindServiceAsync(
                    slash < 0 ? null : qualified[..slash],
                    slash < 0 ? qualified : qualified[(slash + 1)..],
                    cancellation)
                .ConfigureAwait(false);

            if (answer.Service is not { } service
                || !service.IsRunning
                || !service.Limits.AllowsTiles(dataSupportsIt: true)
                || !ServiceFaces.Tileable(service)
                || (answer.Blind && service.Sharing != SharingScope.Public)
                || !LayerAccess.Evaluate(
                    service.Sharing, service.Owner, current.Principal, current.Authorization, service.SharedWith)
                    .IsAllowed())
            {
                continue;
            }

            string url = $"{Origin(context)}/rest/services/{service.QualifiedName}/VectorTileServer";

            layers.Add(new
            {
                id = service.QualifiedName,
                layerType = "VectorTileLayer",
                title = service.QualifiedName,
                url,
                styleUrl = $"{url}/resources/styles/root.json",
                visibility = true,
                opacity = 1,
            });
        }

        return layers.Count > 0
            ? new { id = "graticula-ground", title = "Ground", baseMapLayers = layers }
            : new
            {
                id = "graticula-osm",
                title = "OpenStreetMap",
                baseMapLayers = new object[]
                {
                    new { id = "osm", layerType = "OpenStreetMap", title = "OpenStreetMap", visibility = true, opacity = 1 },
                },
            };
    }

    /// <summary>The organisation, as this caller sees it.</summary>
    /// <remarks>
    /// <b>The identity is the server's, not a per-request accident.</b> A portal's
    /// id is a thing clients cache and compare, so it is derived from the origin
    /// rather than generated — two requests to the same deployment must describe
    /// the same portal or a client concludes it has moved.
    /// </remarks>
    /// <param name="context">The request.</param>
    /// <param name="directory">Where groups live, for the nested user document.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The portal document.</returns>
    private static async Task<IResult> PortalSelfAsync(
        HttpContext context,
        IGroupDirectory directory,
        CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        bool signedIn = current.Principal != Principal.Anonymous;

        // <b>No read for an anonymous caller, which is the common case here.</b>
        // Pro fetches this document before it signs in, and MineAsync answers an
        // anonymous principal without touching the directory.
        IReadOnlyList<object> groups =
            await MineAsync(context, directory, cancellation).ConfigureAwait(false);

        // <b>The geometry service is offered only to a caller it would answer — 2026-09-15.</b> This
        // named it for everybody, and on a deployment where it is not shared with everyone an
        // anonymous client followed the link to a 404: the Maps SDK's measure and project tools
        // take their geometry service from here, and failed with nothing to say why. The same
        // sharing rule the service applies to itself decides, so the link and the service agree.
        SystemService? geometry = await context.RequestServices
            .GetRequiredService<Graticula.Platform.Postgres.PostgresSystemServices>()
            .FindAsync(GeometryServerEndpoints.ServiceName, cancellation)
            .ConfigureAwait(false);

        bool geometryOffered = geometry is { Status: not ServiceStatus.Stopped } found
            && LayerAccess.Evaluate(found.Sharing, null, current.Principal, current.Authorization).IsAllowed();

        // ADR-149: another server's geometry service an administrator chose is named to everybody — that server
        // answers under its own sharing, as ArcGIS's Utility Services setting has it.
        string? elsewhere = null;

        try
        {
            elsewhere = (await context.RequestServices.GetRequiredService<Graticula.Platform.Admin.IServerSettingStore>()
                .ReadAsync(AdminEndpoints.GeometryServiceSetting, cancellation).ConfigureAwait(false))?.Value;
        }
        catch (System.Data.Common.DbException)
        {
            // The store unreachable: this server's own, as before the setting existed.
        }

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
            // <b>`RequireHttps`, the same fact every token response now reports as `ssl`.</b> True on
            // every deployment that did not turn HTTPS off, which is the value this always had.
            allSSL = context.RequestServices.GetRequiredService<HostSettings>().RequireHttps,

            // <b>False although the server has OAuth, until Pro has signed in with it on</b> —
            // ADR-076 §6 and its condition 3. The flow is served and the Maps SDK completes it
            // without reading this flag; what the flag changes in Pro's sign-in is not known, and Pro
            // is the client this server lost three times to a capability it advertised. The cost is
            // named rather than hidden: Field Maps and Survey123 read it too (condition 4).
            supportsOAuth = false,
            supportsHostedServices = true,

            // <b>The ports the caller reached, not the defaults — V-58.</b> A client that rebuilds a
            // URL from these two and the host would otherwise drop the showcase's 8443.
            httpPort = PortOf(context, https: false),
            httpsPort = PortOf(context, https: true),
            currentVersion = PortalVersion,
            access = "public",
            user = signedIn ? Self(current, groups) : null,

            // <b>Pro asks where the geometry service is rather than assuming.</b>
            // We have one (ADR-022) and it is at the address every ArcGIS client
            // looks for, so naming it here is free.
            // <b>The ground the operator chose, where ArcGIS keeps it — Q-110, ADR-086.</b> Map Viewer and
            // the Maps SDK read a portal's default basemap from here, and so do this server's own map
            // pages, so one Save changes the ground everywhere a map is drawn from this portal.
            defaultBasemap = await DefaultBasemapAsync(context, current, cancellation).ConfigureAwait(false),

            helperServices = elsewhere is { Length: > 0 }
                ? (object)new { geometry = new { url = elsewhere } }
                : geometryOffered
                ? new
                {
                    geometry = new
                    {
                        url = $"{Origin(context)}/rest/services/Utilities/Geometry/GeometryServer",
                    },
                }
                : new { },
        });
    }

    /// <summary>The organisation named by id, which is the one this server is.</summary>
    /// <remarks>
    /// <b>The same document as <c>portals/self</c>, deliberately.</b> A second
    /// description of one organisation is a second thing to keep in step, and this
    /// server has exactly one — so the id is checked and the answer is the same
    /// document, rather than a copy assembled beside it.
    /// </remarks>
    /// <param name="context">The request.</param>
    /// <param name="id">The organisation asked for.</param>
    /// <param name="directory">Where groups live, for the nested user document.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The portal document, or a refusal.</returns>
    private static async Task<IResult> OrganizationAsync(
        HttpContext context,
        string id,
        IGroupDirectory directory,
        CancellationToken cancellation)
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

        return await PortalSelfAsync(context, directory, cancellation).ConfigureAwait(false);
    }

    /// <param name="context">The request.</param>
    /// <param name="directory">Where groups live.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The caller's own user document.</returns>
    private static async Task<IResult> CommunitySelfAsync(
        HttpContext context,
        IGroupDirectory directory,
        CancellationToken cancellation)
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

        IReadOnlyList<object> groups =
            await MineAsync(context, directory, cancellation).ConfigureAwait(false);

        return Results.Ok(Self(current, groups));
    }

    /// <summary>One user's profile.</summary>
    /// <remarks>
    /// <b>Only the caller's own, and that is a decision rather than a shortcut.</b>
    /// A portal lets members look each other up; this server has no such surface
    /// and inventing one here would publish the member list through a door nobody
    /// reviewed. Asking about somebody else gets the same answer as asking about a
    /// name that does not exist, which is the rule every other surface follows.
    /// </remarks>
    /// <param name="context">The request.</param>
    /// <param name="username">Who is being asked about.</param>
    /// <param name="directory">Where groups live.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The profile.</returns>
    private static async Task<IResult> UserAsync(
        HttpContext context,
        string username,
        IGroupDirectory directory,
        CancellationToken cancellation)
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

        IReadOnlyList<object> groups =
            await MineAsync(context, directory, cancellation).ConfigureAwait(false);

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

            // <b>The same groups <see cref="GroupsAsync"/> lists, from the same
            // read.</b> This was an empty array until 2026-09-09 with a comment
            // admitting it was not a claim that the account has no groups — which is
            // precisely what a client reads an empty array as. A field that has to
            // be explained in a comment is a field that is lying to somebody who
            // cannot read the comment.
            groups,
        });
    }

    /// <summary>The signed-in caller, as a portal describes one.</summary>
    /// <remarks>
    /// <b>The group list is here rather than on one document, and that is the point
    /// of putting it here.</b> A portal client reads the caller's groups from
    /// whichever of <c>portals/self</c>, <c>community/self</c> and
    /// <c>community/users/{{name}}</c> it happens to use, and a decision that shows
    /// up in one of the three is a decision that looks unimplemented from the other
    /// two. This carried no group list at all until 2026-09-09 — absence rather than
    /// a false claim, but absence is what a client reads as *none*.
    /// </remarks>
    /// <param name="current">Who is asking.</param>
    /// <param name="groups">Their groups, from <see cref="MineAsync"/>.</param>
    /// <returns>The user document.</returns>
    private static object Self(RequestPrincipal current, IReadOnlyList<object> groups) => new
    {
        username = current.Principal.Name,
        fullName = current.Principal.Name,
        access = "private",
        groups,

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
    /// The caller's groups, which are [ADR-036](../../docs/adr/ADR-036-groups.md)'s
    /// groups and not a second kind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Owner decision, 2026-09-09: *gruplarımızı göster*.</b> This answered with
    /// an empty list until then, and the emptiness was the problem —
    /// [ADR-036](../../docs/adr/ADR-036-groups.md)'s groups are real, a service can
    /// be shared with one, and *this portal has no groups* was therefore false. A
    /// 404 was worse: it says *this is not a portal*. What was missing was never
    /// code but a decision about whether ours **are** portal groups or merely
    /// resemble them, and the decision is that they are, published read-only, with
    /// the fields this server actually holds and none invented to fill a shape.
    /// [ADR-040](../../docs/adr/ADR-040-the-portal-surface-is-how-arcgis-pro-connects.md)
    /// §4a carries the mapping and the four places it is imperfect.
    /// </para>
    /// <para>
    /// <b>Read-only, and that is the boundary rather than an omission.</b> A portal
    /// creates, joins and shares through this surface; here a group is made on the
    /// admin API, where the privilege model that governs it lives. Accepting a
    /// create here would be a second write path to the same table with a different
    /// authorization story, which is how two surfaces come to disagree.
    /// </para>
    /// <para>
    /// <b>Anonymous gets an empty list, and that answer is now true.</b> No group is
    /// visible to an anonymous caller — [Q-119](../../docs/open-questions.md)
    /// removed the *anybody* visibility on 2026-08-25, so the widest a group gets is
    /// the signed-in organisation. It is a fact about this deployment rather than a
    /// shape, which is exactly what the whole answer used to be.
    /// </para>
    /// </remarks>
    /// <param name="context">The request.</param>
    /// <param name="directory">Where groups live.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The listing.</returns>
    private static async Task<IResult> GroupsAsync(
        HttpContext context,
        IGroupDirectory directory,
        CancellationToken cancellation)
    {
        IReadOnlyList<object> mine =
            await MineAsync(context, directory, cancellation).ConfigureAwait(false);

        string query = context.Request.Query["q"].ToString();

        if (query.Length == 0 && context.Request.HasFormContentType)
        {
            IFormCollection form = await context.Request.ReadFormAsync(cancellation)
                .ConfigureAwait(false);

            query = form["q"].ToString();
        }

        // <b>The same reader the item search uses, unchanged.</b> It works by
        // reflection over the object that will be serialised, so a group is filtered
        // by the fields a group publishes — and a clause it cannot evaluate returns
        // nothing rather than everything, which is the rule that keeps `type:Feature
        // Service` from matching every group on the server.
        List<object> results = [.. mine.Where(group => PortalQuery.Matches(group, query))];

        return Results.Ok(new
        {
            query,
            total = results.Count,
            start = 1,
            num = results.Count,

            // -1 means there is no next page, which is true: this returns every
            // group the caller may see. The item search says the same and for the
            // same reason — paging arrives when a deployment has enough for it to
            // matter, and a server whose scale target is 100-1,000 services has
            // fewer groups than that (§82).
            nextStart = -1,
            results,
        });
    }

    /// <summary>
    /// The groups this caller may see, as portal groups.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Never widened for an administrator, and the admin API is.</b>
    /// <c>ListAsync</c>'s second argument is *list every group on the server*, and
    /// <c>/admin/groups</c> passes it when the caller holds
    /// <c>admin:manageAllContent</c>. This face passes false whoever is asking,
    /// because Pro files the result under *My Groups* — an administrator who saw
    /// every group here would be shown other people's groups as their own.
    /// </para>
    /// <para>
    /// <b>Groups the caller is outside are included, and they carry it.</b> The
    /// directory already returns organisation-visible groups to a non-member with
    /// <see cref="GroupStanding.Outside"/>, which is portal discoverability exactly.
    /// ADR-036 §4g's line — seeing that a group exists is not reading what is in it
    /// — is kept by what <see cref="Group"/> publishes: no member list, no item
    /// list, and no counts of either.
    /// </para>
    /// </remarks>
    /// <param name="context">The request.</param>
    /// <param name="directory">Where groups live.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The groups, oldest listing order preserved.</returns>
    private static async Task<IReadOnlyList<object>> MineAsync(
        HttpContext context,
        IGroupDirectory directory,
        CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (current.Principal.IsAnonymous)
        {
            return [];
        }

        IReadOnlyList<GroupSummary> mine = await directory
            .ListAsync(current.Principal.Id, all: false, cancellation)
            .ConfigureAwait(false);

        return [.. mine.Select(group => Group(context, group))];
    }

    /// <summary>
    /// One of our groups, in the shape a portal client reads.
    /// </summary>
    /// <remarks>
    /// <b>The mapping is <see cref="PortalGroup"/>, and it is a type of its own for
    /// <see cref="PortalQuery"/>'s reason.</b> It needs nothing from a request but
    /// the caller's name and the portal's id, so keeping it here would make a web
    /// host the only way to assert it — and it is the part with four documented
    /// imperfections to hold still.
    /// </remarks>
    /// <param name="context">The request, for the caller and the portal id.</param>
    /// <param name="group">The group.</param>
    /// <returns>The portal group.</returns>
    private static object Group(HttpContext context, GroupSummary group) => PortalGroup.Of(
        group,
        PortalId(context),
        context.Features.Get<RequestPrincipal>()!.Principal.Name);

    /// <summary>One user's content, which is the items they own.</summary>
    /// <remarks>
    /// <para>
    /// <b>*Items they own*, since 2026-09-09 — it was every item they could see, and
    /// that is what a portal calls something else.</b> A portal's content listing is
    /// per-owner; this answered with everything visible, so Pro's *My Content* showed
    /// a colleague's services beside the caller's own and the two sets coincided only
    /// on a single-operator deployment. [Q-127](../../docs/open-questions.md).
    /// </para>
    /// <para>
    /// <b>A service with no owner belongs to nobody rather than to everybody.</b>
    /// `PublishedService.Owner` is null for anything published before ownership
    /// existed, and those are excluded here — they appear in `search`, where the
    /// question being asked is *what is there* rather than *what is mine*.
    /// </para>
    /// </remarks>
    private static Task<IResult> UserContentAsync(
        HttpContext context,
        CatalogFallback catalog,
        IWebMapStore maps,
        ICoverageCatalog coverages,
        IContentFolderStore folders,
        string username,
        CancellationToken cancellation) =>
        ListUserContentAsync(context, catalog, maps, coverages, folders, username, null, cancellation);

    /// <summary>One of the caller's content folders — ADR-114.</summary>
    private static Task<IResult> UserFolderContentAsync(
        HttpContext context,
        CatalogFallback catalog,
        IWebMapStore maps,
        ICoverageCatalog coverages,
        IContentFolderStore folders,
        string username,
        string folderId,
        CancellationToken cancellation) =>
        Guid.TryParse(folderId, out Guid id)
            ? ListUserContentAsync(context, catalog, maps, coverages, folders, username, id, cancellation)
            : Task.FromResult(Unknown("Folder"));

    private static async Task<IResult> ListUserContentAsync(
        HttpContext context,
        CatalogFallback catalog,
        IWebMapStore maps,
        ICoverageCatalog coverages,
        IContentFolderStore folders,
        string username,
        Guid? folderId,
        CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (current.Principal == Principal.Anonymous
            || !string.Equals(current.Principal.Name, username, StringComparison.OrdinalIgnoreCase))
        {
            return Unknown("User");
        }

        IReadOnlyList<PublishedService>? visible =
            await VisibleAsync(context, catalog, cancellation).ConfigureAwait(false);

        IReadOnlyList<PublishedCoverage>? images =
            await VisibleCoveragesAsync(context, coverages, cancellation).ConfigureAwait(false);

        if (visible is null || images is null)
        {
            return Unavailable();
        }

        IReadOnlyList<ContentFolder> mine = await folders.ListAsync(current.Principal.Id, cancellation).ConfigureAwait(false);
        ContentFolder? here = folderId is { } asked ? mine.FirstOrDefault(f => f.Id == asked) : null;

        if (folderId is not null && here is null)
        {
            return Unknown("Folder");
        }

        // <b>Owned, not visible.</b> `VisibleAsync` has already applied sharing, so
        // this narrows a set the caller may see to the subset they published — which
        // is the whole difference between *My Content* and *the catalogue*.
        //
        // <b>And in the folder asked for — ADR-114</b>: the root lists what is in no folder, as Portal's does.
        List<object> items =
        [
            .. visible
                .Where(service => service.Owner is { } owner && owner == current.Principal.Id
                    && service.ContentFolder == folderId)
                .SelectMany(service => ItemsOf(context, service).Select(face => face.Item)),

            // <b>And the maps they saved — ADR-079</b>, which have an owner of their own and no
            // service behind them.
            .. (await ReadableMapsAsync(context, maps, cancellation).ConfigureAwait(false))
                .Where(map => map.Owner == current.Principal.Id && map.ContentFolder == folderId)
                .Select(map => MapItem(context, map)),

            // And the image services they registered — V-80 — in the content folder they were moved to (ADR-114).
            .. images
                .Where(coverage => coverage.Owner is { } owner && owner == current.Principal.Id
                    && coverage.ContentFolder == folderId)
                .Select(coverage => CoverageItem(context, coverage)),
        ];

        return Results.Ok(new
        {
            username,
            total = items.Count,
            start = 1,
            num = items.Count,
            nextStart = -1,
            currentFolder = here is null ? null : Folder(here, current.Principal.Name),
            items,
            folders = mine.Select(f => Folder(f, current.Principal.Name)),
        });
    }

    /// <summary>A content folder as Portal writes one.</summary>
    private static object Folder(ContentFolder folder, string username) => new
    {
        username,
        id = folder.Id.ToString("N"),
        title = folder.Title,
        created = folder.Created.ToUnixTimeMilliseconds(),
    };

    /// <summary>The signed-in caller, when they are the user named in the path; otherwise null.</summary>
    private static RequestPrincipal? Self(HttpContext context, string username)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        return current.Principal == Principal.Anonymous
            || !string.Equals(current.Principal.Name, username, StringComparison.OrdinalIgnoreCase)
            ? null
            : current;
    }

    private static IResult PortalError(int code, string message) => Results.Json(
        new { error = new { code, message, details = Array.Empty<string>() } });

    /// <summary>Portal's <c>createFolder</c> — ADR-114.</summary>
    private static async Task<IResult> CreateFolderAsync(
        HttpContext context, IContentFolderStore folders, string username, CancellationToken cancellation)
    {
        if (VectorTileExportEndpoints.CrossSiteByCookie(context) is not null)
        {
            return PortalError(403, "Send a token to change your content from another site.");
        }

        if (Self(context, username) is not { } current)
        {
            return Unknown("User");
        }

        IFormCollection form = await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false);
        string title = form["title"].ToString().Trim();

        if (title.Length is 0 or > 128)
        {
            return PortalError(400, "A folder's title is between 1 and 128 characters.");
        }

        if (await folders.CreateAsync(current.Principal.Id, title, cancellation).ConfigureAwait(false) is not { } made)
        {
            return PortalError(409, $"Folder '{title}' already exists.");
        }

        return Results.Ok(new { success = true, folder = Folder(made, current.Principal.Name) });
    }

    /// <summary>Portal's folder <c>delete</c> — only when it is empty (ADR-114 §5.2).</summary>
    private static async Task<IResult> DeleteFolderAsync(
        HttpContext context, IContentFolderStore folders, string username, string folderId, CancellationToken cancellation)
    {
        if (VectorTileExportEndpoints.CrossSiteByCookie(context) is not null)
        {
            return PortalError(403, "Send a token to change your content from another site.");
        }

        if (Self(context, username) is not { } current
            || !Guid.TryParse(folderId, out Guid id)
            || await folders.FindAsync(id, cancellation).ConfigureAwait(false) is not { } folder
            || folder.Owner != current.Principal.Id)
        {
            return Unknown("Folder");
        }

        return await folders.DeleteAsync(folder.Id, cancellation).ConfigureAwait(false) == ContentFolderWrite.NotEmpty
            ? PortalError(409, $"Folder '{folder.Title}' is not empty. Move its items out first; deleting a folder never deletes an item here.")
            : Results.Ok(new { success = true, folder = Folder(folder, current.Principal.Name) });
    }

    /// <summary>Portal's item <c>move</c> — ADR-114.</summary>
    private static async Task<IResult> MoveItemAsync(
        HttpContext context, IContentFolderStore folders, CatalogFallback published, IWebMapStore maps,
        string username, string id, CancellationToken cancellation)
    {
        if (VectorTileExportEndpoints.CrossSiteByCookie(context) is not null)
        {
            return PortalError(403, "Send a token to change your content from another site.");
        }

        if (Self(context, username) is not { } current)
        {
            return Unknown("User");
        }

        IFormCollection form = await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false);
        (Guid? to, IResult? refused) = await MoveTargetAsync(form["folder"].ToString(), current, folders, cancellation).ConfigureAwait(false);

        if (refused is not null)
        {
            return refused;
        }

        string? error = await MoveOneAsync(id, to, current, folders, published, maps, context.RequestServices.GetService(typeof(ICoverageCatalog)) as ICoverageCatalog, cancellation).ConfigureAwait(false);

        return error is null
            ? Results.Ok(new { success = true, itemId = id, owner = current.Principal.Name, folder = to?.ToString("N") })
            : PortalError(400, error);
    }

    /// <summary>Portal's <c>moveItems</c> — ADR-114, one result per item.</summary>
    private static async Task<IResult> MoveItemsAsync(
        HttpContext context, IContentFolderStore folders, CatalogFallback published, IWebMapStore maps,
        string username, CancellationToken cancellation)
    {
        if (VectorTileExportEndpoints.CrossSiteByCookie(context) is not null)
        {
            return PortalError(403, "Send a token to change your content from another site.");
        }

        if (Self(context, username) is not { } current)
        {
            return Unknown("User");
        }

        IFormCollection form = await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false);
        (Guid? to, IResult? refused) = await MoveTargetAsync(form["folder"].ToString(), current, folders, cancellation).ConfigureAwait(false);

        if (refused is not null)
        {
            return refused;
        }

        List<object> results = [];

        foreach (string id in form["items"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string? error = await MoveOneAsync(id, to, current, folders, published, maps, context.RequestServices.GetService(typeof(ICoverageCatalog)) as ICoverageCatalog, cancellation).ConfigureAwait(false);
            results.Add(error is null
                ? new { itemId = id, success = true, error = (object?)null }
                : new { itemId = id, success = false, error = (object?)new { code = 400, message = error } });
        }

        return Results.Ok(new { results });
    }

    /// <summary>The folder a move names: <c>/</c> or empty for the root, or one of the caller's folders.</summary>
    private static async Task<(Guid? To, IResult? Refused)> MoveTargetAsync(
        string raw, RequestPrincipal current, IContentFolderStore folders, CancellationToken cancellation)
    {
        string folder = raw.Trim();

        if (folder.Length == 0 || folder == "/")
        {
            return (null, null);
        }

        return Guid.TryParse(folder, out Guid id)
            && await folders.FindAsync(id, cancellation).ConfigureAwait(false) is { } found
            && found.Owner == current.Principal.Id
            ? (id, null)
            : (null, Unknown("Folder"));
    }

    /// <summary>Moves one of the caller's own items by its portal id, or says why not.</summary>
    private static async Task<string?> MoveOneAsync(
        string id, Guid? to, RequestPrincipal current, IContentFolderStore folders, CatalogFallback published,
        IWebMapStore maps, ICoverageCatalog? coverages, CancellationToken cancellation)
    {
        if (Guid.TryParse(id, out Guid serviceId)
            && (await published.ListServicesAsync(cancellation).ConfigureAwait(false)).Services?.FirstOrDefault(s => s.Id == serviceId) is { } service)
        {
            return service.Owner != current.Principal.Id
                ? "Item does not exist or is inaccessible."
                : await folders.MoveServiceAsync(service.Id, to, cancellation).ConfigureAwait(false) ? null : "Item does not exist or is inaccessible.";
        }

        // An image service's item id is derived from its coverage (ADR-129: it moved nowhere, being looked for among
        // feature services only).
        if (coverages is not null
            && (await coverages.ListAsync(cancellation).ConfigureAwait(false))
                .FirstOrDefault(c => string.Equals(CoverageItemId(c), id, StringComparison.OrdinalIgnoreCase)) is { } image)
        {
            return image.Owner != current.Principal.Id
                ? "Item does not exist or is inaccessible."
                : await folders.MoveServiceAsync(image.ServiceId, to, cancellation).ConfigureAwait(false) ? null : "Item does not exist or is inaccessible.";
        }

        return await maps.FindAsync(id, cancellation).ConfigureAwait(false) is { } map && map.Owner == current.Principal.Id
            ? (await folders.MoveMapAsync(map.Id, to, cancellation).ConfigureAwait(false) ? null : "Item does not exist or is inaccessible.")
            : "Item does not exist or is inaccessible.";
    }

    /// <summary>The catalogue cannot be read and nothing is remembered.</summary>
    /// <remarks>
    /// <b>Not <see cref="Unknown"/>, which is what this would otherwise have become.</b> Every
    /// answer on this face is a filtered listing, so an unreadable catalogue used to arrive as
    /// an empty one — a search with no results, an item that *does not exist or is
    /// inaccessible*. Both are claims, and during an outage both are false. 503 is the only
    /// answer that says *ask again*. [D-127](../../docs/architecture-debt.md).
    /// </remarks>
    /// <returns>The refusal.</returns>
    private static IResult Unavailable() => Results.Json(
        new
        {
            error = new
            {
                code = 503,
                message =
                    "The catalogue is not reachable and this server has no remembered listing "
                    + "to answer from, so it cannot say what it publishes. Retry shortly; see "
                    + "/healthz/ready.",
                details = Array.Empty<string>(),
            },
        },
        statusCode: StatusCodes.Status503ServiceUnavailable);

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

    /// <summary>One portal item, with what it was made from.</summary>
    /// <remarks>
    /// <b>The source rides with the item because a second reader needs it</b> — OGC API Records (ADR-177)
    /// links a record to its service's other faces, and which faces a service answers is a property of the
    /// service, not of the item a portal client reads. Exactly one of <paramref name="Service"/>,
    /// <paramref name="Map"/> and <paramref name="Coverage"/> is set.
    /// </remarks>
    /// <param name="Id">The item's id.</param>
    /// <param name="Item">The item, as a portal search writes it.</param>
    /// <param name="Service">The service it is a face of, or null.</param>
    /// <param name="Face">Which face of <paramref name="Service"/> it is: FeatureServer, MapServer or VectorTileServer.</param>
    /// <param name="Map">The saved web map it is, or null.</param>
    /// <param name="Coverage">The image service it is, or null.</param>
    internal sealed record PortalListed(
        string Id,
        object Item,
        PublishedService? Service = null,
        string? Face = null,
        WebMap? Map = null,
        PublishedCoverage? Coverage = null);

    /// <summary>
    /// Every portal item this caller may see that satisfies a query, or null when a catalogue cannot be read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The one listing, read by the portal's search and by OGC API Records — ADR-177, 2026-10-06.</b> It was
    /// the body of <see cref="SearchAsync"/>; it moved here so that a record exists exactly when this search
    /// would show its item to the same caller, which a second listing could only promise.
    /// </para>
    /// <para>
    /// <b>Services, then saved web maps, then image services</b>, each under its own face's sharing rule, as the
    /// search has always listed them. <c>group:</c> reaches a service's groups only, as it did before the move.
    /// </para>
    /// </remarks>
    /// <param name="context">The request, whose caller decides what is visible.</param>
    /// <param name="catalog">The service catalogue.</param>
    /// <param name="maps">The saved web maps.</param>
    /// <param name="coverages">The image services.</param>
    /// <param name="query">A portal search query, or empty for everything.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The items, or null — the caller answers 503 in its own words (D-127).</returns>
    internal static async Task<IReadOnlyList<PortalListed>?> ListAsync(
        HttpContext context,
        CatalogFallback catalog,
        IWebMapStore maps,
        ICoverageCatalog coverages,
        string? query,
        CancellationToken cancellation)
    {
        IReadOnlyList<PublishedService>? visible =
            await VisibleAsync(context, catalog, cancellation).ConfigureAwait(false);

        IReadOnlyList<PublishedCoverage>? images =
            await VisibleCoveragesAsync(context, coverages, cancellation).ConfigureAwait(false);

        if (visible is null || images is null)
        {
            return null;
        }

        List<PortalListed> results = [];

        foreach (PublishedService service in visible)
        {
            foreach (string face in FacesOf(service))
            {
                object item = Item(context, service, face);

                if (PortalQuery.Matches(item, query, service.SharedWith))
                {
                    results.Add(new PortalListed(ItemIdOf(service, face), item, Service: service, Face: face));
                }
            }
        }

        // Saved web maps, under the same query and the same sharing rule (ADR-079 §5.3).
        foreach (WebMap map in await ReadableMapsAsync(context, maps, cancellation).ConfigureAwait(false))
        {
            object item = MapItem(context, map);

            if (PortalQuery.Matches(item, query))
            {
                results.Add(new PortalListed(map.Id, item, Map: map));
            }
        }

        // Image services, under the same query and the rule their own face reads by — V-80.
        foreach (PublishedCoverage coverage in images)
        {
            object item = CoverageItem(context, coverage);

            if (PortalQuery.Matches(item, query))
            {
                results.Add(new PortalListed(CoverageItemId(coverage), item, Coverage: coverage));
            }
        }

        return results;
    }

    /// <summary>Published services, as portal items.</summary>
    private static async Task<IResult> SearchAsync(
        HttpContext context,
        CatalogFallback catalog,
        IWebMapStore maps,
        ICoverageCatalog coverages,
        CancellationToken cancellation)
    {
        string query = context.Request.Query["q"].ToString();

        if (query.Length == 0 && context.Request.HasFormContentType)
        {
            IFormCollection form = await context.Request.ReadFormAsync(cancellation)
                .ConfigureAwait(false);

            query = form["q"].ToString();
        }

        if (await ListAsync(context, catalog, maps, coverages, query, cancellation).ConfigureAwait(false)
            is not { } listed)
        {
            return Unavailable();
        }

        List<object> results = [.. listed.Select(entry => entry.Item)];

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
        CatalogFallback catalog,
        ServiceContexts contexts,
        Graticula.Geometries.IProjector projector,
        IWebMapStore maps,
        ICoverageCatalog coverages,
        string id,
        CancellationToken cancellation)
    {
        // <b>A saved web map first, because it does not need the catalogue</b> — ADR-079. One the
        // caller may not read falls through to the services and ends at the same refusal as an id
        // that names nothing.
        if (await ReadableMapAsync(context, maps, id, cancellation).ConfigureAwait(false) is { } map)
        {
            return Results.Ok(MapItem(context, map, MapExtent(map.Document)));
        }

        IReadOnlyList<PublishedService>? visible =
            await VisibleAsync(context, catalog, cancellation).ConfigureAwait(false);

        if (visible is null)
        {
            return Unavailable();
        }

        foreach (PublishedService service in visible)
        {
            foreach ((string itemId, object _) in ItemsOf(context, service))
            {
                if (string.Equals(itemId, id, StringComparison.OrdinalIgnoreCase))
                {
                    double[][] extent = await ExtentAsync(service, contexts, projector, cancellation)
                        .ConfigureAwait(false);

                    return Results.Ok(ItemsOf(context, service, extent)
                        .First(face => string.Equals(face.Id, id, StringComparison.OrdinalIgnoreCase)).Item);
                }
            }
        }

        IReadOnlyList<PublishedCoverage>? images =
            await VisibleCoveragesAsync(context, coverages, cancellation).ConfigureAwait(false);

        if (images is null)
        {
            return Unavailable();
        }

        if (images.FirstOrDefault(coverage => string.Equals(CoverageItemId(coverage), id, StringComparison.OrdinalIgnoreCase))
            is { } image)
        {
            return Results.Ok(CoverageItem(
                context, image, await CoverageExtentAsync(image, projector, cancellation).ConfigureAwait(false)));
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

    /// <summary>The file name an item's <c>thumbnail</c> field names, under <c>info/</c>.</summary>
    internal const string ThumbnailFile = "thumbnail.png";

    /// <summary>
    /// The picture of a service item: its first drawable layer's kept thumbnail.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Visible exactly when the item is.</b> The service is found in the same list
    /// <see cref="ItemAsync"/> searches, so an item the caller may not see has no picture either, and the
    /// answer is the same 404 whether it does not exist or is not visible.
    /// </para>
    /// <para>
    /// <b>The first layer that has geometry and answers <c>Query</c></b>, in the order of its id. ArcGIS draws
    /// an item thumbnail of the whole service; this server draws per layer, and a service's first layer is
    /// what its item page already shows. A service with none — a table only, or every layer refusing
    /// queries — names no thumbnail on its item, so a client does not ask.
    /// </para>
    /// </remarks>
    private static async Task ItemThumbnailAsync(
        HttpContext context,
        CatalogFallback catalog,
        ServiceContexts contexts,
        Graticula.Cartography.IMapCanvasFactory canvases,
        ServiceThumbnails held,
        HostSettings settings,
        IWebMapStore maps,
        string id,
        string file,
        CancellationToken cancellation)
    {
        // ADR-119: a web map's own picture, to whoever may open the map.
        if (string.Equals(file, ThumbnailFile, StringComparison.OrdinalIgnoreCase)
            && (await ReadableMapsAsync(context, maps, cancellation).ConfigureAwait(false))
                .FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)) is { HasThumbnail: true } map
            && await maps.ThumbnailAsync(map.Id, cancellation).ConfigureAwait(false) is { } png)
        {
            await Results.Bytes(png, "image/png").ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        // ADR-126: an image service's picture is the image itself, drawn whole as it is styled.
        if (string.Equals(file, ThumbnailFile, StringComparison.OrdinalIgnoreCase)
            && context.RequestServices.GetService(typeof(ICoverageCatalog)) is ICoverageCatalog coverages
            && (await VisibleCoveragesAsync(context, coverages, cancellation).ConfigureAwait(false) ?? [])
                .FirstOrDefault(c => string.Equals(CoverageItemId(c), id, StringComparison.OrdinalIgnoreCase)) is { } image)
        {
            IServiceProvider services = context.RequestServices;
            await ImageServerEndpoints.PreviewAsync(
                    context, image, image.Style ?? "stretch:auto", 400, 266,
                    (Graticula.Coverages.ICoverageReaderFactory)services.GetService(typeof(Graticula.Coverages.ICoverageReaderFactory))!,
                    canvases,
                    (Graticula.Geometries.IProjector)services.GetService(typeof(Graticula.Geometries.IProjector))!,
                    (ConnectionBudget)services.GetService(typeof(ConnectionBudget))!,
                    settings,
                    cancellation)
                .ConfigureAwait(false);
            return;
        }

        IReadOnlyList<PublishedService>? visible = string.Equals(file, ThumbnailFile, StringComparison.OrdinalIgnoreCase)
            ? await VisibleAsync(context, catalog, cancellation).ConfigureAwait(false)
            : [];

        if (visible is null)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        foreach (PublishedService service in visible)
        {
            if (!ItemsOf(context, service).Any(face => string.Equals(face.Id, id, StringComparison.OrdinalIgnoreCase))
                || Pictured(service) is not { } layer)
            {
                continue;
            }

            ServiceThumbnails.Held? picture = await ThumbnailEndpoints.DrawAndKeepAsync(
                layer, contexts, canvases, held, settings, cancellation).ConfigureAwait(false);

            if (picture is not null)
            {
                await ThumbnailEndpoints.AnswerAsync(context, picture, cancellation).ConfigureAwait(false);
                return;
            }

            break;
        }

        context.Response.StatusCode = StatusCodes.Status404NotFound;
    }

    /// <summary>The layer a service item's picture is drawn from, or null when it has none to draw.</summary>
    /// <param name="service">The service.</param>
    /// <returns>Its first layer with geometry that answers <c>Query</c>.</returns>
    internal static PublishedLayer? Pictured(PublishedService service) =>
        service.Layers
            .Where(layer => layer.Definition.GeometryColumn is { Length: > 0 }
                && !CapabilityCeilings.Refuses(layer, "Query"))
            .OrderBy(layer => layer.LayerIndex)
            .FirstOrDefault();

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
        CatalogFallback catalog,
        IWebMapStore maps,
        ICoverageCatalog coverages,
        string id,
        CancellationToken cancellation)
    {
        // <b>A web map's data is its document, as it was saved</b> — which is what an ArcGIS client
        // reads to open it (ADR-079 §5.3). Written as the stored text rather than re-serialised, so
        // the fields this server does not read reach the client as they were saved.
        if (await ReadableMapAsync(context, maps, id, cancellation).ConfigureAwait(false) is { } map)
        {
            return Results.Content(map.Document ?? "{}", "application/json; charset=utf-8");
        }

        IReadOnlyList<PublishedService>? visible =
            await VisibleAsync(context, catalog, cancellation).ConfigureAwait(false);

        if (visible is null)
        {
            return Unavailable();
        }

        foreach (PublishedService service in visible)
        {
            if (ItemsOf(context, service).Any(face => string.Equals(face.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                return Results.Ok(new { });
            }
        }

        IReadOnlyList<PublishedCoverage>? images =
            await VisibleCoveragesAsync(context, coverages, cancellation).ConfigureAwait(false);

        if (images is null)
        {
            return Unavailable();
        }

        if (images.Any(coverage => string.Equals(CoverageItemId(coverage), id, StringComparison.OrdinalIgnoreCase)))
        {
            return Results.Ok(new { });
        }

        return Unknown("Item");
    }

    /// <summary>Every service this caller may see, running.</summary>
    /// <summary>Every service this caller may see, or null when the catalogue is unreadable.</summary>
    /// <remarks>
    /// <b>Null rather than empty, because on this face they read the same and mean the
    /// opposite.</b> [D-127](../../docs/architecture-debt.md): the caller answers
    /// <see cref="Unavailable"/> rather than an empty search.
    /// </remarks>
    private static async Task<IReadOnlyList<PublishedService>?> VisibleAsync(
        HttpContext context, CatalogFallback catalog, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        bool seesStopped = current.Authorization.Allows(Privilege.AdminManageServer);

        CatalogListing listing =
            await catalog.ListServicesAsync(cancellation).ConfigureAwait(false);

        if (listing.Services is not { } services)
        {
            return null;
        }

        if (listing.Blind)
        {
            ServiceLookup.SayAge(context, listing.Age);
        }

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

    /// <summary>
    /// Every image service this caller may see, running unless the caller manages the server, or null when
    /// the coverage catalogue cannot be read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>V-80, the owner's decision of 2026-09-23: an image service is a portal item.</b> Coverages live in
    /// their own catalogue (ADR-043), and this face listed only <see cref="PublishedService"/>, so Pro's
    /// portal pane and the Python API's <c>search</c> could not find an ImageServer the directory listed.
    /// </para>
    /// <para>
    /// <b>The rule <c>ImageServerEndpoints.FindAsync</c> reads by</b> — <see cref="LayerAccess.Evaluate"/>
    /// over the coverage's sharing, owner and groups — so an item is listed exactly when its service answers. Its
    /// groups are its service's, shared as a feature service's are (ADR-124).
    /// </para>
    /// <para>
    /// <b>Null rather than empty, for the reason <see cref="VisibleAsync"/> gives</b> (D-127): a listing
    /// that silently lost every image service during an outage would say they do not exist.
    /// </para>
    /// </remarks>
    private static async Task<IReadOnlyList<PublishedCoverage>?> VisibleCoveragesAsync(
        HttpContext context, ICoverageCatalog coverages, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;
        bool seesStopped = current.Authorization.Allows(Privilege.AdminManageServer);

        IReadOnlyList<PublishedCoverage> all;

        try
        {
            all = await coverages.ListAsync(cancellation).ConfigureAwait(false);
        }
        catch (System.Data.Common.DbException)
        {
            return null;
        }

        return
        [
            .. all.Where(coverage =>
                (coverage.Status == ServiceStatus.Started || seesStopped)
                && LayerAccess
                    .Evaluate(coverage.Sharing, coverage.Owner, current.Principal, current.Authorization, coverage.SharedWith)
                    .IsAllowed()),
        ];
    }

    /// <summary>An image service's item id: 32 hex characters derived from the coverage id.</summary>
    /// <remarks>
    /// <b>Derived rather than the coverage id itself</b>, as a further face's is (<see cref="FaceItemId"/>),
    /// so it can never equal a feature service's item id whichever table minted the two GUIDs.
    /// </remarks>
    /// <param name="coverage">The coverage.</param>
    /// <returns>The id.</returns>
    internal static string CoverageItemId(PublishedCoverage coverage) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{coverage.Id:N}/ImageServer")))[..32];

    /// <summary>The type keywords an image service item carries.</summary>
    internal static readonly string[] ImageServiceKeywords =
        ["ArcGIS Server", "Data", "Image Service", "Service"];

    /// <summary>One image service as a portal item.</summary>
    /// <remarks>
    /// <b>Owned by the same rule as a service's</b> (<see cref="Item"/>, Q-127). <b>Its description, tags and
    /// picture are its own</b> — ADR-126: they were written as null, empty and none, so an imagery item in Pro's
    /// portal pane had nothing to say about itself; the picture is the image drawn whole, as Display draws it.
    /// </remarks>
    private static object CoverageItem(HttpContext context, PublishedCoverage coverage, double[][]? extent = null)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        return new
        {
            id = CoverageItemId(coverage),
            owner = !current.Principal.IsAnonymous && coverage.Owner == current.Principal.Id
                ? current.Principal.Name
                : "graticula",
            orgId = PortalId(context),
            title = coverage.ServiceName,
            name = coverage.ServiceName,
            type = "Image Service",
            typeKeywords = context.RequestServices.GetService(typeof(HostSettings)) is HostSettings settings
                && CoverageAdminEndpoints.Uploaded(settings, coverage.Path)
                    ? [.. ImageServiceKeywords, "Hosted Service"]
                    : ImageServiceKeywords,
            description = coverage.Description,
            snippet = coverage.Description,
            tags = coverage.Tags.Concat(coverage.Folder is null ? [] : new[] { coverage.Folder })
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            url = $"{Origin(context)}/rest/services/{coverage.QualifiedName}/ImageServer",
            thumbnail = $"thumbnail/{ThumbnailFile}",
            access = Access(coverage.Sharing),
            spatialReference = (string?)null,
            extent = extent ?? [],
            numViews = Views(context, coverage.ServiceId),
            size = -1,
            created = coverage.Created?.ToUnixTimeMilliseconds(),
            modified = coverage.Modified?.ToUnixTimeMilliseconds(),
        };
    }

    /// <summary>The requests a service has answered — ADR-135 — or zero before any were counted.</summary>
    private static long Views(HttpContext context, Guid serviceId) =>
        (context.RequestServices.GetService(typeof(ServiceUsageCounter)) as ServiceUsageCounter)?.Of(serviceId)?.Total ?? 0;

    /// <summary>An image service's extent in WGS 84, as an item document carries it, or empty.</summary>
    internal static async Task<double[][]> CoverageExtentAsync(
        PublishedCoverage coverage, Graticula.Geometries.IProjector projector, CancellationToken cancellation)
    {
        IReadOnlyList<Graticula.Geometries.Envelope?> geographic = await Graticula.Geometries.GeographicExtents
            .InWgs84Async(projector, [(coverage.Info.Srid, coverage.Info.Extent)], cancellation)
            .ConfigureAwait(false);

        return geographic is [{ } box]
            ? [[box.MinX, box.MinY], [box.MaxX, box.MaxY]]
            : [];
    }

    /// <summary>The type keywords ArcGIS gives a web map made in its own map viewer.</summary>
    /// <remarks>
    /// <b>Not <c>Offline</c>, <c>Collector</c> or <c>Data Editing</c></b>, which say the map was prepared
    /// for a field app; nothing here prepares one, and a client reading them would offer what it cannot do.
    /// </remarks>
    internal static readonly string[] WebMapKeywords =
        ["ArcGIS Online", "Explorer Web Map", "Map", "Online Map", "Web Map"];

    /// <summary>Every saved web map this caller may read — ADR-079, under the rule services are read by.</summary>
    private static async Task<IReadOnlyList<WebMap>> ReadableMapsAsync(
        HttpContext context, IWebMapStore maps, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        return
        [
            .. (await maps.ListAsync(cancellation).ConfigureAwait(false)).Where(map =>
                LayerAccess.Evaluate(map.Sharing, map.Owner, current.Principal, current.Authorization, map.SharedWith).IsAllowed()),
        ];
    }

    /// <summary>One saved web map with its document, if it exists and this caller may read it.</summary>
    private static async Task<WebMap?> ReadableMapAsync(
        HttpContext context, IWebMapStore maps, string id, CancellationToken cancellation)
    {
        string lower = (id ?? string.Empty).ToLowerInvariant();

        if (!WebMaps.IsId(lower))
        {
            return null;
        }

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        return await maps.FindAsync(lower, cancellation).ConfigureAwait(false) is { } map
            && LayerAccess.Evaluate(map.Sharing, map.Owner, current.Principal, current.Authorization, map.SharedWith).IsAllowed()
                ? map
                : null;
    }

    /// <summary>A saved web map as a portal item of type <c>Web Map</c> — ADR-079 §5.3.</summary>
    /// <remarks>
    /// <b>The owner is named by the same rule as a service's</b> (<see cref="Item"/>, Q-127): the
    /// caller's own name on their own map, and the product's on anybody else's, so a public map does not
    /// publish its author's account name to whoever opens it anonymously. <b>No <c>url</c></b>: a web map
    /// is not a service, and its content is at <c>items/{id}/data</c>.
    /// </remarks>
    private static object MapItem(HttpContext context, WebMap map, double[][]? extent = null)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        return new
        {
            id = map.Id,

            // V-66: the organisation an item belongs to, which `orgid:` and `accountid:` search by.
            orgId = PortalId(context),
            owner = !current.Principal.IsAnonymous && map.Owner == current.Principal.Id
                ? current.Principal.Name
                : "graticula",

            // ADR-114: the owner's folder it is in, said only to its owner.
            ownerFolder = !current.Principal.IsAnonymous && map.Owner == current.Principal.Id && map.ContentFolder is { } inFolder
                ? inFolder.ToString("N")
                : null,
            title = map.Title,
            name = (string?)null,
            type = "Web Map",
            typeKeywords = WebMapKeywords,
            // ADR-119: the description when there is one, the summary as before when not; and the picture.
            description = map.Description ?? map.Snippet,
            thumbnail = map.HasThumbnail ? $"thumbnail/{ThumbnailFile}" : null,
            snippet = map.Snippet,
            tags = map.Tags ?? [],
            url = (string?)null,
            access = Access(map.Sharing),
            spatialReference = (string?)null,
            extent = extent ?? [],
            numViews = 0,
            size = -1,
            created = map.Created.ToUnixTimeMilliseconds(),
            modified = map.Modified.ToUnixTimeMilliseconds(),
        };
    }

    /// <summary>
    /// A web map's initial view in WGS 84, as an item carries its extent, or empty when the document
    /// does not say one in a reference this can read without a projector.
    /// </summary>
    /// <remarks>
    /// <b>Only geographic and Web Mercator.</b> Those are what this viewer writes and what ArcGIS's own map
    /// viewer writes; a map saved in another reference keeps its view in its document, where a client
    /// reads it, and its item says <c>[]</c> as ArcGIS does for an extent it does not have.
    /// </remarks>
    /// <param name="document">The Web Map JSON.</param>
    /// <returns><c>[[xmin, ymin], [xmax, ymax]]</c>, or empty.</returns>
    internal static double[][] MapExtent(string? document)
    {
        if (string.IsNullOrEmpty(document))
        {
            return [];
        }

        try
        {
            using JsonDocument parsed = JsonDocument.Parse(document);

            if (!parsed.RootElement.TryGetProperty("initialState", out JsonElement state)
                || !state.TryGetProperty("viewpoint", out JsonElement viewpoint)
                || !viewpoint.TryGetProperty("targetGeometry", out JsonElement box)
                || !box.TryGetProperty("xmin", out JsonElement xmin)
                || !box.TryGetProperty("ymin", out JsonElement ymin)
                || !box.TryGetProperty("xmax", out JsonElement xmax)
                || !box.TryGetProperty("ymax", out JsonElement ymax)
                || xmin.ValueKind != JsonValueKind.Number)
            {
                return [];
            }

            int wkid = box.TryGetProperty("spatialReference", out JsonElement reference)
                && (reference.TryGetProperty("latestWkid", out JsonElement w)
                    || reference.TryGetProperty("wkid", out w))
                && w.ValueKind == JsonValueKind.Number
                    ? w.GetInt32()
                    : 4326;

            double[] corners = [xmin.GetDouble(), ymin.GetDouble(), xmax.GetDouble(), ymax.GetDouble()];

            if (wkid is 3857 or 102100 or 102113 or 900913)
            {
                const double Radius = 6378137.0;

                static double Longitude(double x) => x / Radius * 180.0 / Math.PI;
                static double Latitude(double y) => ((2 * Math.Atan(Math.Exp(y / Radius))) - (Math.PI / 2)) * 180.0 / Math.PI;

                corners = [Longitude(corners[0]), Latitude(corners[1]), Longitude(corners[2]), Latitude(corners[3])];
            }
            else if (wkid != 4326)
            {
                return [];
            }

            return
            [
                [Math.Max(-180, corners[0]), Math.Max(-90, corners[1])],
                [Math.Min(180, corners[2]), Math.Min(90, corners[3])],
            ];
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            return [];
        }
    }

    /// <summary>
    /// Every item a service is in the portal: its own, and one per further face it answers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One item per face since 2026-09-15, as ArcGIS lists a hosted feature layer, its map image
    /// layer and its vector tile layer as three items.</b> A service here answers as a FeatureServer,
    /// a MapServer where it has a drawable layer and a VectorTileServer where every layer tiles — the
    /// directory has listed all three — and the portal offered only the first, so Pro's portal pane had
    /// no vector tile layer or map image layer to add.
    /// </para>
    /// <para>
    /// <b>The service's own id is its primary item's, unchanged</b>, so every item a client already
    /// holds still resolves. A further face's id is derived from the service id and the face name, so it
    /// is the same on every request and on every node, and nothing new is stored.
    /// </para>
    /// </remarks>
    /// <param name="context">The request.</param>
    /// <param name="service">The service.</param>
    /// <param name="extent">The service's WGS 84 extent for the item document, or null on a listing.</param>
    /// <returns>Each item's id with the item.</returns>
    internal static IEnumerable<(string Id, object Item)> ItemsOf(
        HttpContext context, PublishedService service, double[][]? extent = null)
    {
        foreach (string face in FacesOf(service))
        {
            yield return (ItemIdOf(service, face), Item(context, service, face, extent));
        }
    }

    /// <summary>The faces a service is a portal item for, its primary face first.</summary>
    /// <remarks>
    /// <b>Named apart from <see cref="ItemsOf"/> on 2026-10-06</b> so that <see cref="ListAsync"/> can say which
    /// face each item is without parsing it back out of the item's <c>url</c> — ADR-177.
    /// </remarks>
    /// <param name="service">The service.</param>
    /// <returns>FeatureServer or VectorTileServer, then MapServer and VectorTileServer where it answers them.</returns>
    internal static IEnumerable<string> FacesOf(PublishedService service)
    {
        string primary = PrimaryFace(service);

        yield return primary;

        if (primary != "MapServer" && ServiceFaces.Drawable(service))
        {
            yield return "MapServer";
        }

        if (primary != "VectorTileServer" && ServiceFaces.Tileable(service))
        {
            yield return "VectorTileServer";
        }
    }

    /// <summary>The item id of one face of a service: the service's own id for its primary face.</summary>
    /// <param name="service">The service.</param>
    /// <param name="face">The face.</param>
    /// <returns>The id.</returns>
    internal static string ItemIdOf(PublishedService service, string face) =>
        face == PrimaryFace(service) ? ItemId(service) : FaceItemId(service, face);

    /// <summary>
    /// A service's extent in WGS 84, as a portal item carries it: <c>[[xmin, ymin], [xmax, ymax]]</c>, or
    /// empty when no layer's extent is known.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>On the item document and not on a search, deliberately.</b> Each layer's extent comes from its
    /// described shape, which a cold cache reads from the source; a search listing every item would pay
    /// that for every layer on the server to fill a field the listing does not draw. The item document
    /// is what a client reads before adding the layer and zooming to it, so that is where it is paid.
    /// Search results carry <c>[]</c>, which is what ArcGIS sends for an extent it does not have.
    /// </para>
    /// <para>
    /// <b>The four corners projected, the same approximation WMS and WFS publish</b>
    /// (<see cref="Graticula.Geometries.GeographicExtents"/>), and a layer whose source cannot be read
    /// costs its own box and not the item.
    /// </para>
    /// </remarks>
    internal static async Task<double[][]> ExtentAsync(
        PublishedService service,
        ServiceContexts contexts,
        Graticula.Geometries.IProjector projector,
        CancellationToken cancellation)
    {
        List<(int Srid, Graticula.Geometries.Envelope? Extent)> extents = [];

        foreach (PublishedLayer layer in service.Layers)
        {
            try
            {
                (_, Graticula.Features.LayerDescription described) =
                    await contexts.GetAsync(layer, cancellation).ConfigureAwait(false);

                extents.Add((layer.Definition.Srid, described.Extent));
            }
            catch (Exception unreadable) when (unreadable is not OperationCanceledException)
            {
                extents.Add((layer.Definition.Srid, null));
            }
        }

        IReadOnlyList<Graticula.Geometries.Envelope?> geographic = await Graticula.Geometries.GeographicExtents
            .InWgs84Async(projector, extents, cancellation)
            .ConfigureAwait(false);

        Graticula.Geometries.Envelope?[] known = [.. geographic.Where(e => e is not null)];

        if (known.Length == 0)
        {
            return [];
        }

        return
        [
            [known.Min(e => e!.Value.MinX), known.Min(e => e!.Value.MinY)],
            [known.Max(e => e!.Value.MaxX), known.Max(e => e!.Value.MaxY)],
        ];
    }

    private static string PrimaryFace(PublishedService service) =>
        string.Equals(service.Kind, "VectorTileServer", StringComparison.OrdinalIgnoreCase) ? "VectorTileServer" : "FeatureServer";

    /// <summary>The port a portal document names for one scheme: the caller's own for the scheme it used.</summary>
    /// <param name="context">The request.</param>
    /// <param name="https">Which of the two ports.</param>
    /// <returns>The request's port for its own scheme, and the scheme's default for the other.</returns>
    internal static int PortOf(HttpContext context, bool https)
    {
        bool secure = context.Request.IsHttps;
        int fallback = https ? 443 : 80;

        return secure == https ? context.Request.Host.Port ?? fallback : fallback;
    }

    /// <summary>A further face's item id: 32 hex characters derived from the service id and the face.</summary>
    /// <param name="service">The service.</param>
    /// <param name="face">The face.</param>
    /// <returns>The id.</returns>
    internal static string FaceItemId(PublishedService service, string face) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{service.Id:N}/{face}")))[..32];

    private static object Item(HttpContext context, PublishedService service, string face, double[][]? extent = null)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        // <b>The caller's name when the caller owns it, and the product's when they
        // do not.</b> Pro's *My Content* asks for `owner:<username>`, so an item
        // whose owner is a constant is an item that never appears there.
        //
        // <b>A service owned by somebody else is still not attributed to them, and
        // that is a decision rather than a gap — [Q-127](../../docs/open-questions.md),
        // closed 2026-09-09.</b> This surface has no member directory and is reachable
        // anonymously for public items, so naming an owner here would publish
        // usernames to whoever can see the service. What it costs is named instead of
        // hidden: a portal `owner:` search for another member finds nothing, and the
        // product's name in that field means *not yours* rather than *nobody's*. The
        // listing that has to be right is `/content/users/{name}`, and that one now is:
        // it returns what the caller owns, so the field is the caller's own name on
        // every item in it.
        string owner = service.Owner is { } id && id == current.Principal.Id
            ? current.Principal.Name
            : "graticula";

        return new
        {
            id = ItemIdOf(service, face),
            owner,

            // ADR-114: the owner's folder it is in — said only to its owner, as the owner is.
            ownerFolder = owner == current.Principal.Name && service.ContentFolder is { } inFolder ? inFolder.ToString("N") : null,

            // <b>The organisation it belongs to — V-66, the fourth ArcGIS review.</b> A portal item carries its
            // `orgId`, and `orgid:<id>` — which Pro's *My Organization* and the Python API's default search
            // add — matched nothing here, because the field was not on the item, so an organisation-scoped
            // search answered an empty portal. This portal is one organisation, and its id is portals/self's.
            orgId = PortalId(context),
            title = service.Name,
            name = service.Name,
            type = face switch
            {
                "VectorTileServer" => "Vector Tile Service",
                "MapServer" => "Map Service",
                _ => "Feature Service",
            },

            // <b>Pro reads these to decide what an item is before it opens it.</b>
            // An item with no type keywords is one it will not offer to add.
            //
            // <b>"Hosted Service" only for a service whose every layer this server made — 2026-09-15.</b>
            // It was on every item, so a service over a registered PostGIS table or a GeoParquet
            // file was offered to Pro as hosted, and Pro's hosted-only actions (overwrite, append,
            // delete data with the item) pointed at somebody else's database or at a file.
            typeKeywords = Keywords(
                face, service.Layers.Count > 0 && service.Layers.All(l => l.Definition.IsHosted), service.ViewOf is not null),
            description = service.Description,
            snippet = service.Description,
            // ADR-111: the service's own tags; the folder stays as one, as it always was.
            tags = service.Tags.Concat(service.Folder is null ? [] : new[] { service.Folder })
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            url = $"{Origin(context)}/rest/services/{service.QualifiedName}/{face}",

            // Relative to the item's `info/`, as a portal's is — V-50; null when there is nothing to draw.
            thumbnail = Pictured(service) is null ? null : $"thumbnail/{ThumbnailFile}",
            access = Access(service.Sharing),
            spatialReference = (string?)null,

            // [[xmin, ymin], [xmax, ymax]] in WGS 84 on the item document, [] on a listing (ExtentAsync).
            extent = extent ?? [],
            numViews = Views(context, service.Id),  // ADR-135: the requests its service has answered.
            size = -1,

            // <b>Epoch milliseconds, as every portal date is.</b> Absent until 2026-09-15 although the
            // catalogue has always stamped both, so Pro's *My Content* could not sort by date.
            created = service.Created?.ToUnixTimeMilliseconds(),
            modified = service.Modified?.ToUnixTimeMilliseconds(),
        };
    }

    /// <summary>The type keywords for an item, with <c>Hosted Service</c> only where it is true.</summary>
    /// <param name="face">The face the item is: FeatureServer, MapServer or VectorTileServer.</param>
    /// <param name="hosted">Whether every layer in it is hosted.</param>
    /// <param name="view">Whether it is a view of another service (ADR-113).</param>
    /// <returns>The keywords.</returns>
    internal static string[] Keywords(string face, bool hosted, bool view = false)
    {
        string[] keywords = face switch
        {
            "VectorTileServer" => ["ArcGIS Server", "Data", "Service", "Vector Tile Service"],
            "MapServer" => ["ArcGIS Server", "Data", "Map Service", "Service"],
            _ => ["ArcGIS Server", "Data", "Feature Access", "Feature Service", "Service"],
        };

        keywords = hosted ? [.. keywords, "Hosted Service"] : keywords;

        // ADR-113: ArcGIS Pro reads this to keep Overwrite off a view.
        return view && face == "FeatureServer" ? [.. keywords, "View Service"] : keywords;
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
